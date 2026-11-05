using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    public static class SpatialPoseInitializer
    {
        public static bool TryInitialize(List<SpatialCorrespondence> points, SpatialCameraIntrinsics intrinsics,
            float threshold, out Vector3 position, out Quaternion rotation, out List<SpatialCorrespondence> inliers,
            SpatialInitializationTrace trace = null)
        {
            position = default; rotation = Quaternion.identity; inliers = new List<SpatialCorrespondence>();
            if (points.Count < 8 || points.Count > 300 || !intrinsics.IsValid)
            { if (trace != null) trace.inputFailure = "Insufficient correspondences, resource bound, or invalid intrinsics"; return false; }
            var random = new System.Random(173);
            var bestError = double.PositiveInfinity;
            for (var attempt = 0; attempt < 192; attempt++)
            {
                var indices = new HashSet<int>();
                while (indices.Count < 6) indices.Add(random.Next(points.Count));
                var sample = new List<SpatialCorrespondence>();
                foreach (var index in indices) sample.Add(points[index]);
                var hypothesis = new SpatialHypothesisTrace { attempt = attempt };
                foreach (var point in sample) hypothesis.sampleIds.Add(point.landmarkId);
                if (!TraceLinearPose(sample, intrinsics, out var candidatePosition, out var candidateRotation, hypothesis))
                { trace?.Record(hypothesis); continue; }
                hypothesis.reached.Add("Camera-space depth");
                hypothesis.reached.Add("Reprojection");
                var accepted = new List<SpatialCorrespondence>();
                double error = 0;
                foreach (var point in points)
                {
                    var projected = PnPPoseSolver.Project(point.worldPoint, candidatePosition, candidateRotation, intrinsics);
                    var distance = Vector2.Distance(projected.pixel, point.imagePoint);
                    if (!projected.valid) { hypothesis.invalidDepth++; continue; }
                    if (distance > threshold) { hypothesis.reprojectionRejected++; continue; }
                    accepted.Add(point); error += distance * distance;
                }
                hypothesis.inliers = accepted.Count;
                hypothesis.reached.Add("Inlier consensus");
                hypothesis.outcome = accepted.Count >= 8 && accepted.Count >= points.Count * .5f ? "Accepted consensus"
                    : hypothesis.invalidDepth == points.Count ? "Negative/invalid camera-space depth"
                    : hypothesis.reprojectionRejected == points.Count ? "Reprojection rejection" : "Insufficient inliers";
                trace?.Record(hypothesis);
                if (accepted.Count < inliers.Count || (accepted.Count == inliers.Count && error >= bestError)) continue;
                inliers = accepted; bestError = error; position = candidatePosition; rotation = candidateRotation;
                if (inliers.Count == points.Count) break;
            }
            if (inliers.Count >= 8 && inliers.Count >= points.Count * .5f) return true;
            // DLT's unconstrained projection is unstable on some valid noisy minimal
            // sets. Keep its existing full-set rank/conditioning gate, then generate
            // rigid calibrated candidates directly instead of relaxing row scales.
            var configuration = new SpatialHypothesisTrace();
            if (!TraceLinearPose(points, intrinsics, out _, out _, configuration, true))
            { if (trace != null) trace.failureReason = "Initialization geometry: " + configuration.outcome; return false; }
            var initialized = SpatialThreePointInitializer.TryInitialize(points, intrinsics, threshold, out position, out rotation, out inliers, trace);
            if (!initialized && trace != null) trace.failureReason = "No consistent pose: best hypothesis supports "
                + trace.bestHypothesisInliers + "/" + points.Count + " matches; need " + Math.Max(8, (points.Count + 1) / 2) + ".";
            return initialized;
        }

        static bool LinearPose(List<SpatialCorrespondence> points, SpatialCameraIntrinsics intrinsics,
            out Vector3 position, out Quaternion rotation)
            => TraceLinearPose(points, intrinsics, out position, out rotation, new SpatialHypothesisTrace());

        static bool TraceLinearPose(List<SpatialCorrespondence> points, SpatialCameraIntrinsics intrinsics,
            out Vector3 position, out Quaternion rotation, SpatialHypothesisTrace trace, bool configurationOnly = false)
        {
            position = default; rotation = Quaternion.identity;
            trace.reached.Add("Input correspondence validation");
            trace.reached.Add("Spatial scale");
            var center = Vector3.zero;
            foreach (var point in points) center += point.worldPoint;
            center /= points.Count;
            var scale = 0f;
            foreach (var point in points) scale += (point.worldPoint - center).sqrMagnitude;
            scale = Mathf.Sqrt(scale / points.Count);
            if (scale < .001f) return trace.Reject("Degenerate spatial scale");
            trace.reached.Add("Normalized DLT construction");
            var normal = new double[12, 12];
            foreach (var point in points)
            {
                var normalized = (point.worldPoint - center) / scale;
                var coordinates = new double[] { normalized.x, normalized.y, normalized.z, 1 };
                var horizontal = (point.imagePoint.x - intrinsics.cx) / intrinsics.fx;
                var vertical = (intrinsics.cy - point.imagePoint.y) / intrinsics.fy;
                var rowHorizontal = new double[12]; var rowVertical = new double[12];
                for (var index = 0; index < 4; index++)
                {
                    rowHorizontal[index] = coordinates[index]; rowHorizontal[index + 8] = -horizontal * coordinates[index];
                    rowVertical[index + 4] = coordinates[index]; rowVertical[index + 8] = -vertical * coordinates[index];
                }
                for (var row = 0; row < 12; row++) for (var column = 0; column < 12; column++)
                    normal[row, column] += rowHorizontal[row] * rowHorizontal[column] + rowVertical[row] * rowVertical[column];
            }
            if (!SmallestEigenvector(normal, out var projection, trace)) return false;
            if (configurationOnly) return true;
            trace.reached.Add("Rotation determinant");
            var first = new Vector3((float)projection[0], (float)projection[1], (float)projection[2]);
            var second = new Vector3((float)projection[4], (float)projection[5], (float)projection[6]);
            var third = new Vector3((float)projection[8], (float)projection[9], (float)projection[10]);
            var determinant = Vector3.Dot(Vector3.Cross(first, second), third);
            trace.determinant = determinant;
            if (Mathf.Abs(determinant) < 1e-9f) return trace.Reject("Invalid rotation: singular determinant");
            var factor = (first.magnitude + second.magnitude + third.magnitude) / 3f * Mathf.Sign(determinant);
            first /= factor; second /= factor; third /= factor;
            trace.reached.Add("Rotation row scales");
            trace.firstScale = first.magnitude; trace.secondScale = second.magnitude; trace.thirdScale = third.magnitude;
            if (Mathf.Abs(first.magnitude - 1) > .25f || Mathf.Abs(second.magnitude - 1) > .25f || Mathf.Abs(third.magnitude - 1) > .25f) return trace.Reject("Invalid rotation: unequal row scales");
            first.Normalize(); second = (second - Vector3.Dot(first, second) * first).normalized;
            var forward = Vector3.Cross(first, second);
            trace.reached.Add("Rotation orthogonality and direction");
            trace.forwardAgreement = Vector3.Dot(forward, third);
            if (forward.sqrMagnitude < .9f || Vector3.Dot(forward, third) < .75f) return trace.Reject("Invalid rotation: nonorthogonal rows");
            var translation = new Vector3((float)projection[3], (float)projection[7], (float)projection[11]) / factor;
            position = center - (first * translation.x + second * translation.y + forward * translation.z) * scale;
            rotation = Quaternion.LookRotation(forward, second);
            trace.reached.Add("Finite rigid camera-to-map pose");
            if (!SpatialFrameGeometry.ValidPose(new SpatialPose(position, rotation, 0))) return trace.Reject("Numerical failure: non-finite or invalid pose");
            trace.outcome = "Linear pose accepted";
            return true;
        }

        static bool SmallestEigenvector(double[,] matrix, out double[] vector, SpatialHypothesisTrace trace)
        {
            vector = null;
            trace.reached.Add("Eigensolver convergence");
            var eigenvectors = new double[12, 12];
            for (var index = 0; index < 12; index++) eigenvectors[index, index] = 1;
            var converged = false;
            for (var iteration = 0; iteration < 2000; iteration++)
            {
                var pivotRow = 0; var pivotColumn = 1; var largest = 0d; var diagonal = 0d;
                for (var row = 0; row < 12; row++)
                {
                    diagonal = Math.Max(diagonal, Math.Abs(matrix[row, row]));
                    for (var column = row + 1; column < 12; column++)
                        if (Math.Abs(matrix[row, column]) > largest) { largest = Math.Abs(matrix[row, column]); pivotRow = row; pivotColumn = column; }
                }
                if (largest < Math.Max(1e-14, diagonal * 1e-12)) { converged = true; break; }
                var angle = .5 * Math.Atan2(2 * matrix[pivotRow, pivotColumn], matrix[pivotColumn, pivotColumn] - matrix[pivotRow, pivotRow]);
                var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
                var first = matrix[pivotRow, pivotRow]; var second = matrix[pivotColumn, pivotColumn]; var cross = matrix[pivotRow, pivotColumn];
                for (var index = 0; index < 12; index++)
                {
                    if (index != pivotRow && index != pivotColumn)
                    {
                        var left = matrix[index, pivotRow]; var right = matrix[index, pivotColumn];
                        matrix[index, pivotRow] = matrix[pivotRow, index] = cosine * left - sine * right;
                        matrix[index, pivotColumn] = matrix[pivotColumn, index] = sine * left + cosine * right;
                    }
                    var leftVector = eigenvectors[index, pivotRow]; var rightVector = eigenvectors[index, pivotColumn];
                    eigenvectors[index, pivotRow] = cosine * leftVector - sine * rightVector;
                    eigenvectors[index, pivotColumn] = sine * leftVector + cosine * rightVector;
                }
                matrix[pivotRow, pivotRow] = cosine * cosine * first - 2 * sine * cosine * cross + sine * sine * second;
                matrix[pivotColumn, pivotColumn] = sine * sine * first + 2 * sine * cosine * cross + cosine * cosine * second;
                matrix[pivotRow, pivotColumn] = matrix[pivotColumn, pivotRow] = 0;
            }
            if (!converged) return trace.Reject("Numerical failure: eigensolver did not converge");
            var order = new List<int>(); for (var index = 0; index < 12; index++) order.Add(index);
            order.Sort((left, right) => matrix[left, left].CompareTo(matrix[right, right]));
            trace.reached.Add("DLT nullspace rank");
            if (matrix[order[1], order[1]] < Math.Abs(matrix[order[11], order[11]]) * 1e-9) return trace.Reject("Degenerate DLT nullspace");
            vector = new double[12];
            trace.reached.Add("Finite eigenvector");
            for (var index = 0; index < 12; index++) { vector[index] = eigenvectors[index, order[0]]; if (!SpatialFrameGeometry.Finite(vector[index])) return trace.Reject("Numerical failure: non-finite eigenvector"); }
            return true;
        }
    }
}

using UnityEngine;

namespace SpatialAI
{
    public struct TriangulationResult
    {
        public bool success;
        public Vector3 position;
        public float reprojectionError;
        public float parallaxDegrees;
        public string failureReason;
    }

    public static class SpatialTriangulator
    {
        public static float MinimumBaseline = .05f, MinimumParallaxDegrees = 1.5f, MaximumDepth = 100f, MaximumReprojectionError = 4f;
        public static TriangulationResult Triangulate(SpatialKeyframe first, Vector2 firstPixel, SpatialKeyframe second, Vector2 secondPixel)
        {
            if (first == null || second == null || !first.intrinsics.IsValid || !second.intrinsics.IsValid
                || !SpatialFrameGeometry.ValidPose(first.cameraPose) || !SpatialFrameGeometry.ValidPose(second.cameraPose)) return Fail("Invalid intrinsics or pose");
            if (Vector3.Distance(first.cameraPose.position, second.cameraPose.position) < MinimumBaseline) return Fail("Insufficient baseline");
            var firstRay = Ray(first, firstPixel); var secondRay = Ray(second, secondPixel);
            var cross = Vector3.Cross(firstRay, secondRay); var denominator = cross.sqrMagnitude;
            var parallax = Vector3.Angle(firstRay, secondRay);
            if (denominator < 1e-8f || parallax < MinimumParallaxDegrees) return Fail("Insufficient parallax");
            var baseline = second.cameraPose.position - first.cameraPose.position;
            var firstDepth = Vector3.Dot(Vector3.Cross(baseline, secondRay), cross) / denominator;
            var secondDepth = Vector3.Dot(Vector3.Cross(baseline, firstRay), cross) / denominator;
            if (firstDepth <= 0 || secondDepth <= 0 || firstDepth > MaximumDepth || secondDepth > MaximumDepth) return Fail("Negative or excessive depth");
            var point = (first.cameraPose.position + firstRay * firstDepth + second.cameraPose.position + secondRay * secondDepth) * .5f;
            if (!SpatialFrameGeometry.Finite(point.x) || !SpatialFrameGeometry.Finite(point.y) || !SpatialFrameGeometry.Finite(point.z)) return Fail("Nonfinite triangulation");
            var error = Mathf.Max(Vector2.Distance(Pixel(first, point), firstPixel), Vector2.Distance(Pixel(second, point), secondPixel));
            if (!SpatialFrameGeometry.Finite(error) || error > MaximumReprojectionError) return Fail("Reprojection exceeds pixel threshold");
            return new TriangulationResult { success = true, position = point, reprojectionError = error, parallaxDegrees = parallax };
        }
        static Vector3 Ray(SpatialKeyframe frame, Vector2 pixel) => (frame.cameraPose.rotation * new Vector3(
            (pixel.x - frame.intrinsics.cx) / frame.intrinsics.fx, (frame.intrinsics.cy - pixel.y) / frame.intrinsics.fy, 1)).normalized;
        public static Vector2 Pixel(SpatialKeyframe frame, Vector3 point)
        {
            var cameraPoint = Quaternion.Inverse(frame.cameraPose.rotation) * (point - frame.cameraPose.position);
            return new Vector2(frame.intrinsics.fx * cameraPoint.x / cameraPoint.z + frame.intrinsics.cx,
                frame.intrinsics.cy - frame.intrinsics.fy * cameraPoint.y / cameraPoint.z);
        }
        static TriangulationResult Fail(string reason) => new TriangulationResult { failureReason = reason };
    }
}

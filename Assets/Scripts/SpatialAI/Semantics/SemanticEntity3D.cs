using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    // Dynamic physical state of a persistent entity (Phase 4: 4D Living Map).
    // Static: confirmed in place across rescans. Moved: same label, shifted
    // beyond the reconciler threshold. NewObstruction: appeared on the active
    // navigation route. Removed: previously mapped, absent on rescan.
    public enum SemanticEntityStatus
    {
        Static = 0,
        Moved = 1,
        NewObstruction = 2,
        Removed = 3
    }

    // One SAM 3.1 detection lifted into the map. Serializable so entities
    // persist in SpatialMapData alongside landmarks via JsonUtility.
    [Serializable]
    public class SemanticEntity3D
    {
        public string entityId;
        public string label;
        public float confidence;
        public Vector3 position;      // Map space, metres.
        public Vector3 extent;        // Estimated bounding size in metres (x, y, z).
        // Capture-camera orientation in map space: extent x/y/z run along
        // this basis, so a portrait-held phone no longer transposes width
        // and height into a sideways box. Identity for legacy entities.
        public Quaternion rotation = Quaternion.identity;
        // Provenance of the depth behind position: "landmark" (triangulated
        // consensus) or "plane" (AR plane raycast fallback). Never estimated.
        public string depthSource = "landmark";
        public float discoveryTimestamp;
        public string discoveredAtUtc;
        public float lastSeenTimestamp;
        public int observationCount;
        public List<string> sourceKeyframeIds = new List<string>();
        // SAM's per-response object id for this sighting (-1 when unknown).
        // Ids are stable only within one response, never across separate
        // requests: the reconciler uses this as a match preference inside
        // its label+radius window, never as a global identity key.
        public int samObjectId = -1;

        // Phase 4 lifecycle / change tracking. Written only by
        // SpatialMapReconciler; defaults keep legacy maps Static.
        public SemanticEntityStatus Status = SemanticEntityStatus.Static;
        public Vector3 PreviousPosition;   // Map space position before the last Moved update.
        public float DisplacementMeters;   // Distance of the last Moved update; 0 otherwise.
        public float LastVerifiedTimestamp; // Realtime timestamp of the last reconciler verification.
    }

    // A single 2D detection in downscaled image pixels, before lifting.
    // Box corners are downscaled-image source pixels (top-left origin),
    // exactly as SAM reports them; the mask raster covers the box rect
    // (maskW columns by maskH rows). SAM provides no depth, so depth always
    // comes from triangulated landmarks - never from estimates.
    public struct SemanticDetection2D
    {
        public string label;
        public float confidence;      // -1 when the record carries none.
        public Rect pixelBox;         // Downscaled image space, top-left origin.
        public byte[] maskRaster;     // Row-major 0/1 over the box rect; null when absent.
        public int maskW;             // Raster columns.
        public int maskH;             // Raster rows.
        public int samObjectId;       // SAM per-response object id; -1 when unknown.
    }

    // Purity-checked gates for SAM detections. Wireframes have shipped on
    // wrong objects and floating in air, so a mask must now prove itself
    // twice: enough agreeing landmark depth (no single stray landmark may
    // place a box), and a coherent mask fill inside its own box (no sliver
    // or empty raster lifted on box geometry alone).
    public static class SpatialDetectionSanity
    {
        // Median depth needs company: fewer samples, or samples that
        // disagree beyond this fraction of the median, reject.
        public const int MinimumDepthLandmarks = 3;
        public const float MaximumDepthSpreadRatio = 0.30f;
        // Mask ones over the box rect below this are noise, not an object.
        public const float MinimumMaskFillRatio = 0.02f;

        public static bool DepthConsensus(List<float> depths, out float median)
        {
            median = 0f;
            if (depths == null || depths.Count < MinimumDepthLandmarks) return false;
            depths.Sort();
            median = depths[depths.Count / 2];
            if (median <= 0.05f) return false;
            float spread = depths[depths.Count - 1] - depths[0];
            return spread <= MaximumDepthSpreadRatio * median;
        }

        public static bool MaskFillSane(byte[] raster, int w, int h, out float fill)
        {
            fill = 0f;
            if (raster == null || w <= 0 || h <= 0 || raster.Length != w * h) return false;
            long ones = 0;
            for (int i = 0; i < raster.Length; i++) if (raster[i] != 0) ones++;
            fill = (float)ones / raster.Length;
            return fill >= MinimumMaskFillRatio;
        }
    }

    public static class SpatialSemanticLifter
    {
        // Projects a metric-space world point into the downscaled image.
        // Metric camera: X right, Y up, Z back (forward = -Z).
        public static bool ProjectMetric(Vector3 worldMetric, SpatialPose metricPose,
            SpatialCameraIntrinsics intrinsics, out Vector2 pixel, out float depth)
        {
            Vector3 cam = Quaternion.Inverse(metricPose.rotation) * (worldMetric - metricPose.position);
            depth = -cam.z;
            pixel = Vector2.zero;
            if (depth <= 0.05f || !intrinsics.IsValid) return false;
            pixel = new Vector2(
                intrinsics.fx * (cam.x / depth) + intrinsics.cx,
                intrinsics.cy - intrinsics.fy * (cam.y / depth));
            return true;
        }

        static Vector3 MetricToMap(Vector3 metric)
        {
            return new Vector3(metric.x, metric.y, -metric.z);
        }

        static Vector3 LandmarkToMetric(Vector3 mapPosition)
        {
            return MetricToMap(mapPosition);
        }

        // Median landmark depth (forward metres) inside the decoded mask.
        // Landmarks live in Unity map space, so they are flipped into the
        // metric frame before projection. A landmark counts only when it
        // projects inside the box AND onto a mask pixel of 1, so depth
        // comes from the segmented object - never the background. Returns
        // false when no triangulated landmark lands on the mask.
        public static bool EstimateDepth(SemanticDetection2D detection, SpatialKeyframePayload payload,
            IReadOnlyList<SpatialLandmark> landmarks, out float depth)
        {
            depth = 0f;
            if (landmarks == null || payload == null) return false;
            var depths = new List<float>();
            bool useMask = HasUsableMask(detection);
            for (int i = 0; i < landmarks.Count; i++)
            {
                var landmark = landmarks[i];
                if (landmark == null) continue;
                Vector2 pixel; float d;
                if (!ProjectMetric(LandmarkToMetric(landmark.position), payload.MetricCameraPose,
                        payload.DownscaledIntrinsics, out pixel, out d)) continue;
                if (!detection.pixelBox.Contains(pixel)) continue;
                if (useMask && SampleMask(detection, pixel) == 0) continue;
                depths.Add(d);
            }
            // Consensus, not bare median: a lone landmark (often background
            // seen through a sloppy mask) must never set a box depth.
            return SpatialDetectionSanity.DepthConsensus(depths, out depth);
        }

        static bool HasUsableMask(SemanticDetection2D detection)
        {
            return detection.maskRaster != null && detection.maskW > 0 && detection.maskH > 0 &&
                detection.maskRaster.Length == detection.maskW * detection.maskH &&
                detection.pixelBox.width > 0f && detection.pixelBox.height > 0f;
        }

        // Mask value (0/1) under a downscaled-image pixel. The raster covers
        // the box rect, so box-space coordinates scale into raster cells.
        static int SampleMask(SemanticDetection2D detection, Vector2 pixel)
        {
            int mx = (int)((pixel.x - detection.pixelBox.xMin) / detection.pixelBox.width * detection.maskW);
            int my = (int)((pixel.y - detection.pixelBox.yMin) / detection.pixelBox.height * detection.maskH);
            if (mx < 0 || my < 0 || mx >= detection.maskW || my >= detection.maskH) return 0;
            return detection.maskRaster[my * detection.maskW + mx];
        }

        // Centroid and tight size of the mask-ones, in box pixel space.
        // Returns false when the mask is absent or empty; the caller then
        // falls back to the box center and box size (box is still genuine
        // SAM geometry, only the pixel weighting is lost).
        public static bool MaskCentroidAndSize(SemanticDetection2D detection,
            out Vector2 boxCenter, out float boxWidth, out float boxHeight)
        {
            boxCenter = detection.pixelBox.center;
            boxWidth = detection.pixelBox.width;
            boxHeight = detection.pixelBox.height;
            if (!HasUsableMask(detection)) return false;
            long sumX = 0, sumY = 0, count = 0;
            int minX = detection.maskW, minY = detection.maskH, maxX = -1, maxY = -1;
            for (int y = 0; y < detection.maskH; y++)
            {
                for (int x = 0; x < detection.maskW; x++)
                {
                    if (detection.maskRaster[y * detection.maskW + x] == 0) continue;
                    sumX += x; sumY += y; count++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
            if (count == 0) return false;
            float cx = (float)sumX / count, cy = (float)sumY / count;
            boxCenter = new Vector2(
                detection.pixelBox.xMin + (cx + 0.5f) / detection.maskW * detection.pixelBox.width,
                detection.pixelBox.yMin + (cy + 0.5f) / detection.maskH * detection.pixelBox.height);
            boxWidth = Mathf.Max(1f, (maxX - minX + 1) / (float)detection.maskW * detection.pixelBox.width);
            boxHeight = Mathf.Max(1f, (maxY - minY + 1) / (float)detection.maskH * detection.pixelBox.height);
            return true;
        }

        // Lifts one detection to a map-space entity position and extent from
        // genuine triangulated landmark depth inside the decoded mask. There
        // is deliberately NO depth fallback: when no landmark lands on the
        // mask this returns false and the caller must skip the entity loudly.
        public static bool TryLift(SemanticDetection2D detection, SpatialKeyframePayload payload,
            IReadOnlyList<SpatialLandmark> landmarks,
            out Vector3 mapPosition, out Vector3 extent, out float usedDepth)
        {
            mapPosition = Vector3.zero; extent = Vector3.zero; usedDepth = 0f;
            if (payload == null || !payload.DownscaledIntrinsics.IsValid) return false;

            float depth;
            if (!EstimateDepth(detection, payload, landmarks, out depth)) return false;
            usedDepth = depth;

            var k = payload.DownscaledIntrinsics;
            Vector2 center; float pixelWidth, pixelHeight;
            MaskCentroidAndSize(detection, out center, out pixelWidth, out pixelHeight);
            mapPosition = UnprojectToMap(center, depth, k, payload.MetricCameraPose);
            extent = BoxExtent(pixelWidth, pixelHeight, depth, k);
            return true;
        }

        // Metric-space point under an image pixel at a forward depth.
        static Vector3 UnprojectMetric(Vector2 center, float depth,
            SpatialCameraIntrinsics k, SpatialPose metricPose)
        {
            var cam = new Vector3(
                (center.x - k.cx) / k.fx * depth,
                (k.cy - center.y) / k.fy * depth,
                -depth);
            return metricPose.position + metricPose.rotation * cam;
        }

        // Map-space point under an image pixel at a forward depth.
        public static Vector3 UnprojectToMap(Vector2 center, float depth,
            SpatialCameraIntrinsics k, SpatialPose metricPose)
        {
            return MetricToMap(UnprojectMetric(center, depth, k, metricPose));
        }

        // Map-space capture-camera orientation: extent axes run along this
        // basis. Same handedness flip as positions (R' = F * R * F).
        public static Quaternion MapRotation(SpatialPose metricPose)
        {
            var r = metricPose.rotation;
            return new Quaternion(-r.x, -r.y, r.z, r.w);
        }

        // Metric box size from pixel size at depth.
        public static Vector3 BoxExtent(float pixelWidth, float pixelHeight,
            float depth, SpatialCameraIntrinsics k)
        {
            float widthM = Mathf.Max(0.05f, pixelWidth / k.fx * depth);
            float heightM = Mathf.Max(0.05f, pixelHeight / k.fy * depth);
            return new Vector3(widthM, heightM, 0.5f * Mathf.Max(widthM, heightM));
        }

        // Capture ray through the mask centroid, in map space: the origin
        // is the capture camera position and the direction has unit length.
        public static bool MapCentroidRay(SemanticDetection2D detection,
            SpatialKeyframePayload payload, out Vector3 originMap, out Vector3 dirMap)
        {
            originMap = Vector3.zero; dirMap = Vector3.forward;
            if (payload == null || !payload.DownscaledIntrinsics.IsValid) return false;
            Vector2 center; float pixelWidth, pixelHeight;
            MaskCentroidAndSize(detection, out center, out pixelWidth, out pixelHeight);
            var k = payload.DownscaledIntrinsics;
            var pose = payload.MetricCameraPose;
            originMap = MetricToMap(pose.position);
            Vector3 delta = MetricToMap(UnprojectMetric(center, 1f, k, pose)) - originMap;
            if (delta.sqrMagnitude < 1e-8f) return false;
            dirMap = delta.normalized;
            return true;
        }
    }
}

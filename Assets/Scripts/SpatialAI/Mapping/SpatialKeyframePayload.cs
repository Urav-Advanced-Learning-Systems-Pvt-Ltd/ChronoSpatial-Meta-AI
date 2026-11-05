using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SpatialAI
{
    // Right-handed metric pose: metres, X right, Y up, Z back.
    // Unity is left-handed (X right, Y up, Z forward), so conversion flips Z.
    public static class SpatialMetricPose
    {
        public const string CoordinateSystem = "right-handed-metres-x-right-y-up-z-back";

        public static SpatialPose FromUnity(SpatialPose pose)
        {
            var p = pose.position;
            var r = pose.rotation;
            // R' = F * R * F with F = diag(1,1,-1): q' = (-x, -y, z, w).
            return new SpatialPose(
                new Vector3(p.x, p.y, -p.z),
                new Quaternion(-r.x, -r.y, r.z, r.w),
                pose.timestamp);
        }

        public static void DownscaleIntrinsics(SpatialCameraIntrinsics source, int maxDimension,
            out SpatialCameraIntrinsics scaled, out int outWidth, out int outHeight)
        {
            int w = Math.Max(1, source.imageWidth);
            int h = Math.Max(1, source.imageHeight);
            double scale = Math.Min(1.0, maxDimension / (double)Math.Max(w, h));
            outWidth = Math.Max(1, (int)Math.Round(w * scale));
            outHeight = Math.Max(1, (int)Math.Round(h * scale));
            double sx = outWidth / (double)w;
            double sy = outHeight / (double)h;
            scaled = new SpatialCameraIntrinsics
            {
                fx = (float)(source.fx * sx),
                fy = (float)(source.fy * sy),
                cx = (float)(source.cx * sx),
                cy = (float)(source.cy * sy),
                imageWidth = outWidth,
                imageHeight = outHeight
            };
        }
    }

    // Sidecar image payload for one accepted keyframe. Never stored on
    // SpatialKeyframe, so map serialization stays free of image bytes.
    // Raw pixels are owned by the payload until the preprocessor releases them.
    public sealed class SpatialKeyframePayload
    {
        public const int MaxImageDimension = 1024;

        public string KeyframeId { get; private set; }
        public float Timestamp { get; private set; }
        public SpatialPose CameraPose { get; private set; }
        public SpatialCameraIntrinsics Intrinsics { get; private set; }
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }

        // Filled at creation with pure math only (no pixel work).
        public SpatialCameraIntrinsics DownscaledIntrinsics { get; private set; }
        public int DownscaledWidth { get; private set; }
        public int DownscaledHeight { get; private set; }

        // Raw Unity-thread handoff. Null after preprocessing.
        public Color32[] Pixels { get; internal set; }

        // Filled by SpatialSamPreprocessorWorker on its background thread.
        public byte[] JpegBytes { get; internal set; }
        public SpatialPose MetricCameraPose { get; internal set; }
        public string CoordinateSystem { get; internal set; }
        public string Error { get; internal set; }

        public static SpatialKeyframePayload Create(SpatialKeyframe keyframe, Color32[] pixels)
        {
            if (keyframe == null) throw new ArgumentNullException(nameof(keyframe));
            var intrinsics = keyframe.intrinsics;
            if (intrinsics.imageWidth != keyframe.imageWidth || intrinsics.imageHeight != keyframe.imageHeight)
            {
                intrinsics.imageWidth = keyframe.imageWidth;
                intrinsics.imageHeight = keyframe.imageHeight;
            }
            SpatialCameraIntrinsics scaled; int outW, outH;
            SpatialMetricPose.DownscaleIntrinsics(intrinsics, MaxImageDimension,
                out scaled, out outW, out outH);
            return new SpatialKeyframePayload
            {
                KeyframeId = keyframe.keyframeId,
                Timestamp = keyframe.timestamp,
                CameraPose = keyframe.cameraPose,
                Intrinsics = keyframe.intrinsics,
                ImageWidth = keyframe.imageWidth,
                ImageHeight = keyframe.imageHeight,
                DownscaledIntrinsics = scaled,
                DownscaledWidth = outW,
                DownscaledHeight = outH,
                Pixels = pixels
            };
        }
    }

    // Thread-safe bounded queue. Offer never blocks: when full, the oldest
    // item is dropped so a slow consumer can never stall SLAM.
    public sealed class SpatialBoundedQueue<T> where T : class
    {
        readonly object sync = new object();
        readonly Queue<T> items;
        readonly AutoResetEvent available = new AutoResetEvent(false);
        readonly int capacity;
        long dropped;

        public SpatialBoundedQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
            items = new Queue<T>(capacity);
        }

        public int Capacity { get { return capacity; } }
        public int Count { get { lock (sync) return items.Count; } }
        public long DroppedCount { get { lock (sync) return dropped; } }

        // Returns the dropped oldest item, or null. Never waits.
        public T Offer(T item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            T droppedItem = null;
            lock (sync)
            {
                if (items.Count >= capacity) { droppedItem = items.Dequeue(); dropped++; }
                items.Enqueue(item);
            }
            available.Set();
            return droppedItem;
        }

        // Blocks up to timeoutMs for an item. Returns false on timeout.
        public bool TryTake(out T item, int timeoutMs, CancellationToken token)
        {
            while (true)
            {
                lock (sync)
                {
                    if (items.Count > 0) { item = items.Dequeue(); return true; }
                }
                if (token.IsCancellationRequested) { item = null; return false; }
                if (!available.WaitOne(Math.Max(1, timeoutMs))) { item = null; return false; }
            }
        }

        public void Clear()
        {
            lock (sync) items.Clear();
        }
    }

}

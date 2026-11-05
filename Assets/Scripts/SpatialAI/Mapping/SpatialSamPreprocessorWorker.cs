using System;
using System.Threading;
using UnityEngine;

namespace SpatialAI
{
    // Background SAM 3.1 preprocessor. Pulls raw keyframe payloads from the
    // bounded sidecar queue, downscales to MaxImageDimension, JPEG-encodes
    // with the pure managed encoder, and converts the pose to right-handed
    // metres. No Unity texture APIs and no network run here or on the
    // Unity thread; processed payloads wait in a second bounded queue.
    public sealed class SpatialSamPreprocessorWorker : IDisposable
    {
        public const int QueueCapacity = 4;
        public const int JpegQuality = 82;

        readonly SpatialBoundedQueue<SpatialKeyframePayload> input =
            new SpatialBoundedQueue<SpatialKeyframePayload>(QueueCapacity);
        readonly SpatialBoundedQueue<SpatialKeyframePayload> processed =
            new SpatialBoundedQueue<SpatialKeyframePayload>(QueueCapacity);
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly Thread thread;
        bool disposed;
        long processedCount;
        string lastError;

        public SpatialSamPreprocessorWorker()
        {
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SpatialAI.SamPreprocess",
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            thread.Start();
        }

        public int PendingCount { get { return input.Count; } }
        public int ProcessedCount { get { return processed.Count; } }
        public long DroppedCount { get { return input.DroppedCount; } }
        public long TotalProcessed { get { return Interlocked.Read(ref processedCount); } }
        public string LastError { get { return lastError; } }

        // Never blocks. Returns false only for null. Drops oldest when full.
        public bool TryOffer(SpatialKeyframePayload payload)
        {
            if (payload == null || disposed) return false;
            input.Offer(payload);
            return true;
        }

        public bool TryTakeProcessed(out SpatialKeyframePayload payload)
        {
            // Non-blocking drain for the future SAM upload client.
            if (processed.Count == 0) { payload = null; return false; }
            return processed.TryTake(out payload, 0, CancellationToken.None);
        }

        void Run()
        {
            while (!cancellation.IsCancellationRequested)
            {
                SpatialKeyframePayload payload;
                if (!input.TryTake(out payload, 100, cancellation.Token)) continue;
                try
                {
                    Process(payload);
                    processed.Offer(payload);
                    Interlocked.Increment(ref processedCount);
                }
                catch (Exception e)
                {
                    payload.Error = e.Message;
                    payload.Pixels = null;
                    lastError = e.Message;
                }
            }
        }

        static void Process(SpatialKeyframePayload payload)
        {
            var pixels = payload.Pixels;
            int w = payload.ImageWidth, h = payload.ImageHeight;
            if (pixels == null || w <= 0 || h <= 0 || pixels.Length < w * h)
                throw new InvalidOperationException("Missing or truncated keyframe pixels");

            int outW = payload.DownscaledWidth, outH = payload.DownscaledHeight;
            var rgb = new byte[outW * outH * 3];
            // GetPixels32 row 0 is the bottom row; flip so JPEG is top-down.
            for (int y = 0; y < outH; y++)
            {
                int topDownRow = (int)((long)y * h / outH);
                int srcRow = h - 1 - topDownRow;
                for (int x = 0; x < outW; x++)
                {
                    int srcX = (int)((long)x * w / outW);
                    Color32 c = pixels[srcRow * w + srcX];
                    int i = (y * outW + x) * 3;
                    rgb[i] = c.r; rgb[i + 1] = c.g; rgb[i + 2] = c.b;
                }
            }
            payload.JpegBytes = SpatialJpegEncoder.Encode(rgb, outW, outH, JpegQuality);
            payload.MetricCameraPose = SpatialMetricPose.FromUnity(payload.CameraPose);
            payload.CoordinateSystem = SpatialMetricPose.CoordinateSystem;
            payload.Pixels = null; // Release the raw frame as early as possible.
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            // Do not block Unity waiting for an in-flight encode.
            thread.Join(100);
            cancellation.Dispose();
        }
    }
}

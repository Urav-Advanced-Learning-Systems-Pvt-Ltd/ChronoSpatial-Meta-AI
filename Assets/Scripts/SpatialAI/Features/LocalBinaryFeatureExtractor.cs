using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI {
    /// <summary>Small deterministic CPU fallback: gradient corners plus binary patch descriptors.</summary>
    public sealed class LocalBinaryFeatureExtractor : ISpatialFeatureExtractor {
        public int MaxFeatures = 300;
        public int Border = 12;
        public int CellSize = 16;
        public SpatialFeatureSet Extract(Texture2D frame) {
            if (frame == null) return new SpatialFeatureSet();
            return Extract(frame.GetPixels32(), frame.width, frame.height);
        }
        // Owned managed pixels only. Keep arithmetic and row convention identical to Texture2D extraction.
        public SpatialFeatureSet Extract(Color32[] pixels, int width, int height,
            System.Threading.CancellationToken cancellation = default) {
            var result = new SpatialFeatureSet { imageWidth = width, imageHeight = height };
            var border = Mathf.Max(Border, BinaryPatchDescriptor.Radius);
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length != (long)width * height
                || CellSize <= 0 || width < border * 2 + 1 || height < border * 2 + 1) return result;
            var gray = new float[width * height];
            for (var i = 0; i < pixels.Length; i++) {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                gray[i] = (pixels[i].r * 0.299f + pixels[i].g * 0.587f + pixels[i].b * 0.114f) / 255f;
            }
            var candidates = new List<Candidate>();
            for (var y = border; y < height - border; y += 2) for (var x = border; x < width - border; x += 2) {
                var dx = gray[y * width + x + 1] - gray[y * width + x - 1];
                var dy = gray[(y + 1) * width + x] - gray[(y - 1) * width + x];
                var score = dx * dx + dy * dy;
                if (score > 0.015f) candidates.Add(new Candidate { x=x, y=y, score=score });
            }
            candidates.Sort((a,b) => b.score.CompareTo(a.score));
            var occupied = new HashSet<Vector2Int>();
            for (var i = 0; i < candidates.Count && result.features.Count < MaxFeatures; i++) {
                cancellation.ThrowIfCancellationRequested();
                var c = candidates[i]; var cell = new Vector2Int(c.x / CellSize, c.y / CellSize); if (!occupied.Add(cell)) continue;
                result.features.Add(new SpatialFeature { featureId = "f-" + result.features.Count.ToString("D4"), imagePoint = new Vector2(c.x, c.y), descriptor = BinaryPatchDescriptor.Describe(gray, width, height, c.x, c.y), descriptorSize = BinaryPatchDescriptor.Bytes, descriptorType = BinaryPatchDescriptor.Type, quality = c.score });
            }
            return result;
        }
        struct Candidate { public int x; public int y; public float score; }
    }
}

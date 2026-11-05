using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace SpatialAI
{
    // Native ORB owns no Unity object or retained pointer. Rows stay in CPU-image order.
    public sealed class OrbFeatureExtractor : ISpatialFeatureExtractor
    {
        [DllImport("spatial_orb", CallingConvention = CallingConvention.Cdecl)]
        static extern int spatial_orb_extract([In] Color32[] rgba, int width, int height,
            [Out] float[] points, [Out] byte[] descriptors, int capacity);
        public SpatialFeatureSet Extract(Texture2D image) => image == null
            ? new SpatialFeatureSet() : Extract(image.GetPixels32(), image.width, image.height);
        public SpatialFeatureSet Extract(Color32[] pixels, int width, int height, CancellationToken cancellation = default)
        {
            if (pixels == null || width <= 0 || height <= 0 || width > 4096 || height > 4096 || pixels.Length != (long)width * height)
                throw new ArgumentException("Invalid ORB image dimensions");
            cancellation.ThrowIfCancellationRequested();
            var points = new float[300 * 3]; var descriptors = new byte[300 * 32];
            int count;
            try { count = spatial_orb_extract(pixels, width, height, points, descriptors, 300); }
            catch (DllNotFoundException e) { throw new InvalidOperationException("Offline ORB native library is unavailable on this platform", e); }
            cancellation.ThrowIfCancellationRequested();
            if (count < 0 || count > 300) throw new InvalidOperationException("Offline ORB extraction failed: " + count);
            var result = new SpatialFeatureSet { imageWidth = width, imageHeight = height };
            for (var i = 0; i < count; i++)
            {
                var descriptor = new byte[32]; Array.Copy(descriptors, i * 32, descriptor, 0, 32);
                result.features.Add(new SpatialFeature { featureId = "orb-f-" + i.ToString("D4"),
                    imagePoint = new Vector2(points[i * 3], points[i * 3 + 1]), quality = points[i * 3 + 2],
                    descriptor = descriptor, descriptorSize = 32, descriptorType = SpatialFeatureSchemas.OrbDistinct });
            }
            return result;
        }
    }
}

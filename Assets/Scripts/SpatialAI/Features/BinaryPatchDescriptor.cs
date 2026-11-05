using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    // This string versions sampling, intensity comparison and LSB-first packing together.
    public static class BinaryPatchDescriptor
    {
        public const string Type = "binary-patch-v2-xs32-6d2b79f5-r8";
        public const int Radius = 8;
        public const int Bits = 256;
        public const int Bytes = 32;
        public const string IncompatibleMessage = "Incompatible descriptor pattern. Preserve this map and make a fresh scan; original images are not stored, so descriptors cannot be regenerated.";
        public readonly struct Pair
        {
            public readonly Vector2Int a, b;
            public Pair(int first, int second) { a = new Vector2Int(first % 17 - Radius, first / 17 - Radius); b = new Vector2Int(second % 17 - Radius, second / 17 - Radius); }
        }
        static readonly Pair[] pattern = GeneratePattern();
        public static Pair GetPair(int bit) => pattern[bit];

        // Integer-only specified PRNG, independent of Unity/System.Random state and runtime version.
        static uint Next(ref uint state) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        public static Pair[] GeneratePattern()
        {
            uint state = 0x6d2b79f5;
            var result = new Pair[Bits];
            var seen = new HashSet<int>();
            for (var bit = 0; bit < Bits;)
            {
                var a = (int)(Next(ref state) % 289); var b = (int)(Next(ref state) % 289);
                if (a == b || !seen.Add(Math.Min(a, b) * 289 + Math.Max(a, b))) continue;
                result[bit++] = new Pair(a, b);
            }
            return result;
        }
        // Distinct unordered pairs do not imply statistically independent descriptor bits.
        public static byte[] Describe(float[] gray, int width, int height, int x, int y)
        {
            if (gray == null || width <= 0 || height <= 0 || (long)width * height != gray.Length
                || x < Radius || y < Radius || x >= width - Radius || y >= height - Radius)
                throw new ArgumentException("Descriptor patch lies outside the supplied image");
            var descriptor = new byte[Bytes];
            for (var bit = 0; bit < Bits; bit++)
            {
                var pair = pattern[bit];
                if (gray[(y + pair.a.y) * width + x + pair.a.x] < gray[(y + pair.b.y) * width + x + pair.b.x])
                    descriptor[bit / 8] |= (byte)(1 << (bit % 8));
            }
            return descriptor;
        }
    }
}

using System;

namespace SpatialAI
{
    // Exact 256-bit Hamming arithmetic; no hardware-specific or package dependency.
    public static class PackedDescriptor
    {
        public static ulong[] Pack(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 32) throw new ArgumentException("Expected 32 descriptor bytes");
            return new[] { BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8),
                BitConverter.ToUInt64(bytes, 16), BitConverter.ToUInt64(bytes, 24) };
        }

        public static int PopCount(ulong value)
        {
            unchecked
            {
                value -= (value >> 1) & 0x5555555555555555UL;
                value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
                value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
                return (int)((value * 0x0101010101010101UL) >> 56);
            }
        }

        public static int Distance(ulong[] left, ulong[] right) => PopCount(left[0] ^ right[0])
            + PopCount(left[1] ^ right[1]) + PopCount(left[2] ^ right[2]) + PopCount(left[3] ^ right[3]);
    }
}

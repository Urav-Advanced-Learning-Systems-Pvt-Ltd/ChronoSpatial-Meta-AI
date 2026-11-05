using System;
using System.IO;

namespace SpatialAI
{
    // Pure managed baseline JPEG encoder (4:4:4, quality-scaled Annex K tables).
    // No Unity APIs, so it is safe on background threads where
    // Texture2D.EncodeToJPG / ImageConversion would be illegal.
    public static class SpatialJpegEncoder
    {
        static readonly byte[] Zigzag =
        {
            0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,
            27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,
            44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63
        };
        static readonly int[] LumaQuant =
        {
            16,11,10,16,24,40,51,61, 12,12,14,19,26,58,60,55,
            14,13,16,24,40,57,69,56, 14,17,22,29,51,87,80,62,
            18,22,37,56,68,109,103,77, 24,35,55,64,81,104,113,92,
            49,64,78,87,103,121,120,101, 72,92,95,98,112,100,103,99
        };
        static readonly int[] ChromaQuant =
        {
            17,18,24,47,99,99,99,99, 18,21,26,66,99,99,99,99,
            24,26,56,99,99,99,99,99, 47,66,99,99,99,99,99,99,
            99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99,
            99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99
        };
        // Standard Huffman tables (JPEG Annex K).
        static readonly byte[] DcLumaBits = { 0,1,5,1,1,1,1,1,1,0,0,0,0,0,0,0 };
        static readonly byte[] DcLumaVals = { 0,1,2,3,4,5,6,7,8,9,10,11 };
        static readonly byte[] DcChromaBits = { 0,3,1,1,1,1,1,1,1,1,1,0,0,0,0,0 };
        static readonly byte[] DcChromaVals = { 0,1,2,3,4,5,6,7,8,9,10,11 };
        static readonly byte[] AcLumaBits = { 0,2,1,3,3,2,4,3,5,5,4,4,0,0,1,0x7d };
        static readonly byte[] AcLumaVals =
        {
            0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,
            0x22,0x71,0x14,0x32,0x81,0x91,0xa1,0x08,0x23,0x42,0xb1,0xc1,0x15,0x52,0xd1,0xf0,
            0x24,0x33,0x62,0x72,0x82,0x09,0x0a,0x16,0x17,0x18,0x19,0x1a,0x25,0x26,0x27,0x28,
            0x29,0x2a,0x34,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,0x49,
            0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
            0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
            0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,0xa6,0xa7,
            0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,0xc4,0xc5,
            0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,0xe1,0xe2,
            0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf1,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
            0xf9,0xfa
        };
        static readonly byte[] AcChromaBits = { 0,2,1,2,4,4,3,4,7,5,4,4,0,1,2,0x77 };
        static readonly byte[] AcChromaVals =
        {
            0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,
            0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,0xa1,0xb1,0xc1,0x09,0x23,0x33,0x52,0xf0,
            0x15,0x62,0x72,0xd1,0x0a,0x16,0x24,0x34,0xe1,0x25,0xf1,0x17,0x18,0x19,0x1a,0x26,
            0x27,0x28,0x29,0x2a,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,
            0x49,0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,
            0x69,0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x82,0x83,0x84,0x85,0x86,0x87,
            0x88,0x89,0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,
            0xa6,0xa7,0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,
            0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,
            0xe2,0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
            0xf9,0xfa
        };

        sealed class HuffTable
        {
            public readonly int[] Code = new int[256];
            public readonly int[] Size = new int[256];
            public HuffTable(byte[] bits, byte[] vals)
            {
                int code = 0, k = 0;
                for (int len = 1; len <= 16; len++)
                {
                    for (int i = 0; i < bits[len - 1]; i++)
                    {
                        int sym = vals[k++];
                        Code[sym] = code; Size[sym] = len; code++;
                    }
                    code <<= 1;
                }
            }
        }

        sealed class BitWriter
        {
            readonly MemoryStream stream;
            int buffer, bits;
            public BitWriter(MemoryStream s) { stream = s; }
            public void Write(int code, int size)
            {
                buffer = (buffer << size) | (code & ((1 << size) - 1));
                bits += size;
                while (bits >= 8)
                {
                    int b = (buffer >> (bits - 8)) & 0xFF;
                    stream.WriteByte((byte)b);
                    if (b == 0xFF) stream.WriteByte(0);
                    bits -= 8;
                }
                buffer &= bits == 0 ? 0 : (1 << bits) - 1;
            }
            public void Flush()
            {
                if (bits > 0) Write((1 << (8 - bits)) - 1, 8 - bits);
            }
        }

        static int[] ScaleQuant(int[] table, int quality)
        {
            quality = Math.Max(1, Math.Min(100, quality));
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
            var result = new int[64];
            for (int i = 0; i < 64; i++)
                result[i] = Math.Max(1, (table[i] * scale + 50) / 100);
            return result;
        }

        static void WriteMarker(MemoryStream s, int marker) { s.WriteByte(0xFF); s.WriteByte((byte)marker); }
        static void Write16(MemoryStream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

        static void WriteQuant(MemoryStream s, int id, int[] quant)
        {
            WriteMarker(s, 0xDB); Write16(s, 67); s.WriteByte((byte)id);
            for (int i = 0; i < 64; i++) s.WriteByte((byte)Math.Min(255, quant[Zigzag[i]]));
        }

        static void WriteHuff(MemoryStream s, int clsId, byte[] bits, byte[] vals)
        {
            WriteMarker(s, 0xC4); Write16(s, 3 + 16 + vals.Length); s.WriteByte((byte)clsId);
            s.Write(bits, 0, 16); s.Write(vals, 0, vals.Length);
        }

        static int Category(int v)
        {
            int a = Math.Abs(v), n = 0;
            while (a > 0) { n++; a >>= 1; }
            return n;
        }

        static void EncodeBlock(BitWriter writer, int[] block, ref int prevDc,
            HuffTable dc, HuffTable ac, int[] quant)
        {
            var coef = new int[64];
            // FDCT on level-shifted samples stored in block (row-major 8x8).
            for (int v = 0; v < 8; v++)
            {
                for (int u = 0; u < 8; u++)
                {
                    double sum = 0;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                            sum += block[y * 8 + x] *
                                Math.Cos((2 * x + 1) * u * Math.PI / 16) *
                                Math.Cos((2 * y + 1) * v * Math.PI / 16);
                    double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                    double cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
                    coef[v * 8 + u] = (int)Math.Round(0.25 * cu * cv * sum / quant[v * 8 + u]);
                }
            }
            var zz = new int[64];
            for (int i = 0; i < 64; i++) zz[i] = coef[Zigzag[i]];

            int diff = zz[0] - prevDc; prevDc = zz[0];
            int cat = Category(diff);
            writer.Write(dc.Code[cat], dc.Size[cat]);
            if (cat > 0)
            {
                int bitsVal = diff < 0 ? diff + (1 << cat) - 1 : diff;
                writer.Write(bitsVal, cat);
            }
            int run = 0;
            for (int i = 1; i < 64; i++)
            {
                if (zz[i] == 0) { run++; continue; }
                while (run > 15)
                {
                    writer.Write(ac.Code[0xF0], ac.Size[0xF0]);
                    run -= 16;
                }
                int c = Category(zz[i]);
                int sym = (run << 4) | c;
                writer.Write(ac.Code[sym], ac.Size[sym]);
                int bitsVal = zz[i] < 0 ? zz[i] + (1 << c) - 1 : zz[i];
                writer.Write(bitsVal, c);
                run = 0;
            }
            if (run > 0) writer.Write(ac.Code[0x00], ac.Size[0x00]);
        }

        // rgb is top-down RGB, length width*height*3. Returns baseline JPEG.
        public static byte[] Encode(byte[] rgb, int width, int height, int quality)
        {
            if (rgb == null || width <= 0 || height <= 0 || rgb.Length < width * height * 3)
                throw new ArgumentException("Invalid RGB input");
            var lumaQuant = ScaleQuant(LumaQuant, quality);
            var chromaQuant = ScaleQuant(ChromaQuant, quality);
            var dcLuma = new HuffTable(DcLumaBits, DcLumaVals);
            var dcChroma = new HuffTable(DcChromaBits, DcChromaVals);
            var acLuma = new HuffTable(AcLumaBits, AcLumaVals);
            var acChroma = new HuffTable(AcChromaBits, AcChromaVals);

            using (var stream = new MemoryStream())
            {
                WriteMarker(stream, 0xD8); // SOI
                WriteMarker(stream, 0xE0); Write16(stream, 16); // APP0
                stream.Write(new byte[] { 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 }, 0, 14);
                WriteQuant(stream, 0, lumaQuant);
                WriteQuant(stream, 1, chromaQuant);
                WriteMarker(stream, 0xC0); Write16(stream, 17); stream.WriteByte(8); // SOF0
                Write16(stream, height); Write16(stream, width); stream.WriteByte(3);
                stream.Write(new byte[] { 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1 }, 0, 9);
                WriteHuff(stream, 0x00, DcLumaBits, DcLumaVals);
                WriteHuff(stream, 0x10, AcLumaBits, AcLumaVals);
                WriteHuff(stream, 0x01, DcChromaBits, DcChromaVals);
                WriteHuff(stream, 0x11, AcChromaBits, AcChromaVals);
                WriteMarker(stream, 0xDA); Write16(stream, 12); stream.WriteByte(3); // SOS
                stream.Write(new byte[] { 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0 }, 0, 9);

                var writer = new BitWriter(stream);
                int prevY = 0, prevCb = 0, prevCr = 0;
                var block = new int[64];
                int blocksX = (width + 7) / 8, blocksY = (height + 7) / 8;
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        for (int comp = 0; comp < 3; comp++)
                        {
                            for (int y = 0; y < 8; y++)
                            {
                                int sy = Math.Min(height - 1, by * 8 + y);
                                for (int x = 0; x < 8; x++)
                                {
                                    int sx = Math.Min(width - 1, bx * 8 + x);
                                    int i = (sy * width + sx) * 3;
                                    int r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                                    int v;
                                    if (comp == 0) v = (int)(0.299 * r + 0.587 * g + 0.114 * b) - 128;
                                    else if (comp == 1) v = (int)(-0.168736 * r - 0.331264 * g + 0.5 * b);
                                    else v = (int)(0.5 * r - 0.418688 * g - 0.081312 * b);
                                    block[y * 8 + x] = v;
                                }
                            }
                            if (comp == 0) EncodeBlock(writer, block, ref prevY, dcLuma, acLuma, lumaQuant);
                            else if (comp == 1) EncodeBlock(writer, block, ref prevCb, dcChroma, acChroma, chromaQuant);
                            else EncodeBlock(writer, block, ref prevCr, dcChroma, acChroma, chromaQuant);
                        }
                    }
                }
                writer.Flush();
                WriteMarker(stream, 0xD9); // EOI
                return stream.ToArray();
            }
        }
    }
}

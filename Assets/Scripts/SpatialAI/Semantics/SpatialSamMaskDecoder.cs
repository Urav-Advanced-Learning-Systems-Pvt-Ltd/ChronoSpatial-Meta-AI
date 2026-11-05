using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SpatialAI
{
    // C# port of Meta's @meta-sam/parser mask-codec (dist/mask-codec.js, 0.1.0)
    // plus a strict parser for the documented special-token grammar:
    //   <Nf>id<|box;x1;..;w;h[;c]|><|mask;x=0;y=0[;c];data=H,W,enc+payload|>(,id...)*
    // The wire alphabet (printable ASCII minus " \ , ; < > |) excludes ',', ';'
    // and '|', so comma record splits and "|>" token scans are unambiguous.
    // All arithmetic mirrors the reference exactly: uint wrap for >>>-style
    // ops, ulong accumulators for base85 groups (85^5-1 exceeds uint32, so the
    // out-of-range checks are meaningful), min-1 rescaling only where the
    // reference applies it (zeroCounts, orderOne - but NOT orderZero).
    public sealed class SamMaskCodecException : Exception
    {
        public SamMaskCodecException(string message) : base(message) { }
        public SamMaskCodecException(string message, Exception inner) : base(message, inner) { }
    }

    // One decoded wire record. The concept label is NOT on the wire: the API
    // returns no labels, so the caller attaches the concept it requested.
    public struct SamRecord
    {
        public int frameIndex;
        public int objectId;
        public int x1, y1, x2, y2, w, h;   // Box corners + source size, source pixels.
        public float confidence;           // Record c, or -1 when absent (absence is not zero).
        public int maskH, maskW;           // Raster rows then columns.
        public char maskEncoding;          // '~' lossless or '!' one_bit.
        public string maskPayload;         // Base85 bytes after the enc marker.
    }

    public static class SamLineParser
    {
        public static List<SamRecord> ParseOutputText(string text)
        {
            var records = new List<SamRecord>();
            if (string.IsNullOrWhiteSpace(text)) return records;
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                records.AddRange(ParseLine(line));
            }
            return records;
        }

        public static List<SamRecord> ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new SamMaskCodecException("SAM line is empty.");
            line = line.Trim();
            int pos = 0;
            if (!Consume(line, ref pos, '<'))
                throw Fail(line, "expected '<Nf>' frame marker.");
            int frame = ParseIntToken(line, ref pos, "frame index");
            if (!Consume(line, ref pos, 'f') || !Consume(line, ref pos, '>'))
                throw Fail(line, "expected '<Nf>' frame marker.");
            var records = new List<SamRecord>();
            if (pos >= line.Length) return records;
            while (true)
            {
                var record = ParseRecord(line, ref pos, frame);
                records.Add(record);
                if (pos >= line.Length) break;
                if (!Consume(line, ref pos, ','))
                    throw Fail(line, "expected ',' between records.");
                if (pos >= line.Length)
                    throw Fail(line, "trailing ',' with no record.");
            }
            return records;
        }

        static SamRecord ParseRecord(string line, ref int pos, int frame)
        {
            var record = new SamRecord { frameIndex = frame, confidence = -1f };
            record.objectId = ParseIntToken(line, ref pos, "object id");
            Expect(line, ref pos, "<|box;");
            int boxEnd = line.IndexOf("|>", pos, StringComparison.Ordinal);
            if (boxEnd < 0) throw Fail(line, "unterminated box token.");
            ParseBoxFields(line.Substring(pos, boxEnd - pos), line, ref record);
            pos = boxEnd + 2;
            Expect(line, ref pos, "<|mask;");
            int maskEnd = line.IndexOf("|>", pos, StringComparison.Ordinal);
            if (maskEnd < 0) throw Fail(line, "unterminated mask token.");
            ParseMaskFields(line.Substring(pos, maskEnd - pos), line, ref record);
            pos = maskEnd + 2;
            return record;
        }

        static void ParseBoxFields(string body, string line, ref SamRecord record)
        {
            foreach (var field in SplitFields(body))
            {
                string key, value;
                SplitKeyValue(field, line, out key, out value);
                switch (key)
                {
                    case "x1": record.x1 = ParseInt(value, line, "box x1"); break;
                    case "y1": record.y1 = ParseInt(value, line, "box y1"); break;
                    case "x2": record.x2 = ParseInt(value, line, "box x2"); break;
                    case "y2": record.y2 = ParseInt(value, line, "box y2"); break;
                    case "w": record.w = ParseInt(value, line, "box w"); break;
                    case "h": record.h = ParseInt(value, line, "box h"); break;
                    case "c": record.confidence = ParseFloat(value, line, "box c"); break;
                    default: throw Fail(line, "unknown box field '" + key + "'.");
                }
            }
            if (record.w <= 0 || record.h <= 0)
                throw Fail(line, "box source size must be positive.");
        }

        static void ParseMaskFields(string body, string line, ref SamRecord record)
        {
            bool hasData = false;
            foreach (var field in SplitFields(body))
            {
                string key, value;
                SplitKeyValue(field, line, out key, out value);
                switch (key)
                {
                    case "x":
                        if (value != "0") throw Fail(line, "mask origin x must be literal 0.");
                        break;
                    case "y":
                        if (value != "0") throw Fail(line, "mask origin y must be literal 0.");
                        break;
                    case "c":
                        record.confidence = ParseFloat(value, line, "mask c");
                        break;
                    case "data":
                        ParseData(value, line, ref record);
                        hasData = true;
                        break;
                    default: throw Fail(line, "unknown mask field '" + key + "'.");
                }
            }
            if (!hasData) throw Fail(line, "mask token has no data field.");
        }

        static void ParseData(string value, string line, ref SamRecord record)
        {
            var parts = value.Split(',');
            if (parts.Length != 3)
                throw Fail(line, "mask data must be H,W,enc+payload.");
            record.maskH = ParseInt(parts[0], line, "mask H");
            record.maskW = ParseInt(parts[1], line, "mask W");
            if (record.maskH <= 0 || record.maskW <= 0)
                throw Fail(line, "mask dimensions must be positive.");
            if (parts[2].Length < 2 || (parts[2][0] != '~' && parts[2][0] != '!'))
                throw Fail(line, "mask encoding marker must be '~' or '!'.");
            record.maskEncoding = parts[2][0];
            record.maskPayload = parts[2].Substring(1);
            if (record.maskPayload.Length == 0)
                throw Fail(line, "mask payload is empty.");
        }

        static string[] SplitFields(string body)
        {
            // ';' is excluded from the payload alphabet, so a split is exact.
            return body.Split(new[] { ';' }, StringSplitOptions.None);
        }

        static void SplitKeyValue(string field, string line, out string key, out string value)
        {
            int eq = field.IndexOf('=');
            if (eq <= 0 || eq == field.Length - 1)
                throw Fail(line, "malformed field '" + field + "'.");
            key = field.Substring(0, eq);
            value = field.Substring(eq + 1);
        }

        static int ParseIntToken(string line, ref int pos, string what)
        {
            int start = pos;
            while (pos < line.Length && line[pos] >= '0' && line[pos] <= '9') pos++;
            if (pos == start) throw Fail(line, "expected " + what + ".");
            int value;
            if (!int.TryParse(line.Substring(start, pos - start), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value))
                throw Fail(line, what + " is not a valid integer.");
            return value;
        }

        static int ParseInt(string value, string line, string what)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                throw Fail(line, what + " is not a valid integer.");
            return parsed;
        }

        static float ParseFloat(string value, string line, string what)
        {
            float parsed;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                throw Fail(line, what + " is not a valid number.");
            return parsed;
        }

        static bool Consume(string line, ref int pos, char expected)
        {
            if (pos < line.Length && line[pos] == expected) { pos++; return true; }
            return false;
        }

        static void Expect(string line, ref int pos, string token)
        {
            if (pos + token.Length > line.Length ||
                string.CompareOrdinal(line.Substring(pos, token.Length), token) != 0)
                throw Fail(line, "expected '" + token + "'.");
            pos += token.Length;
        }

        static SamMaskCodecException Fail(string line, string reason)
        {
            string preview = line.Length > 80 ? line.Substring(0, 80) + "..." : line;
            return new SamMaskCodecException("SAM grammar error: " + reason + " Line: " + preview);
        }
    }

    // Binary raster decoder for the documented mask payloads. Ports the
    // reference algorithms exactly (uint wrap for >>>-style ops, ulong
    // accumulators for base85 groups, min-1 rescaling only on zeroCounts
    // and orderOne - never on orderZero).
    public static class SamMaskCodec
    {
        const int Radix = 85;
        const int PrefixLength = 5;
        const uint Top = 1u << 24;
        const uint Bottom = 1u << 16;
        const uint Mask32 = 0xffffffff;
        const long MaximumPayloadBytes = 64L * 1024 * 1024;
        // Defensive raster cap (reference enforces safe-int only). Real SAM
        // masks are bounded by the sent image; anything larger is rejected
        // loudly instead of allocating blindly.
        const long MaximumRasterArea = 16L * 1024 * 1024;

        static readonly string Alphabet = BuildAlphabet();
        static readonly short[] CharValues = BuildCharValues();

        static string BuildAlphabet()
        {
            var builder = new StringBuilder(94);
            for (int code = 0x21; code <= 0x7e; code++)
            {
                char c = (char)code;
                if (c == '"' || c == '\\' || c == ',' || c == ';' ||
                    c == '<' || c == '>' || c == '|')
                    continue;
                builder.Append(c);
            }
            return builder.ToString();
        }

        static short[] BuildCharValues()
        {
            var table = new short[128];
            for (int i = 0; i < table.Length; i++) table[i] = -1;
            for (int i = 0; i < Alphabet.Length; i++) table[Alphabet[i]] = (short)i;
            return table;
        }

        static int Digit(char c)
        {
            int code = c;
            int value = code < 128 ? CharValues[code] : -1;
            if (value < 0 || value >= Radix)
                throw new SamMaskCodecException("Mask payload contains an invalid character.");
            return value;
        }

        public static string Pack(byte[] input)
        {
            if (input == null)
                throw new SamMaskCodecException("Mask bytes missing for packing.");
            long length = input.Length;
            var prefix = new char[PrefixLength];
            for (int i = PrefixLength - 1; i >= 0; i--)
            {
                prefix[i] = Alphabet[(int)(length % Radix)];
                length /= Radix;
            }
            if (length != 0)
                throw new SamMaskCodecException("Mask payload is too large.");
            var builder = new StringBuilder(input.Length + PrefixLength + 8);
            builder.Append(prefix);
            int offset = 0;
            int full = input.Length - (input.Length % 4);
            for (; offset < full; offset += 4)
            {
                ulong value = ((ulong)input[offset] << 24) | ((ulong)input[offset + 1] << 16) |
                    ((ulong)input[offset + 2] << 8) | input[offset + 3];
                var digits = new int[5];
                for (int i = 4; i >= 0; i--)
                {
                    digits[i] = (int)(value % (ulong)Radix);
                    value /= (ulong)Radix;
                }
                foreach (int d in digits) builder.Append(Alphabet[d]);
            }
            int remainder = input.Length - offset;
            if (remainder > 0)
            {
                ulong value = 0;
                for (int i = 0; i < 4; i++)
                    value = (value << 8) | (ulong)(i < remainder ? input[offset + i] : 0);
                var digits = new int[5];
                for (int i = 4; i >= 0; i--)
                {
                    digits[i] = (int)(value % (ulong)Radix);
                    value /= (ulong)Radix;
                }
                for (int i = 0; i < remainder + 1; i++) builder.Append(Alphabet[digits[i]]);
            }
            return builder.ToString();
        }

        public static byte[] Unpack(string payload)
        {
            if (string.IsNullOrEmpty(payload) || payload.Length < PrefixLength)
                throw new SamMaskCodecException("Mask payload is missing its length prefix.");
            long length = 0;
            for (int i = 0; i < PrefixLength; i++)
                length = length * Radix + Digit(payload[i]);
            long remainder = length % 4;
            long expected = PrefixLength + (length / 4) * 5 + (remainder == 0 ? 0 : remainder + 1);
            if (payload.Length != expected)
                throw new SamMaskCodecException("Mask payload length does not match its prefix.");
            if (length > MaximumPayloadBytes)
                throw new SamMaskCodecException("Mask payload length is unsupported.");
            var output = new byte[length];
            int source = PrefixLength;
            long destination = 0;
            long full = length - remainder;
            for (; destination < full; destination += 4)
            {
                ulong value = 0;
                for (int i = 0; i < 5; i++)
                    value = value * (ulong)Radix + (ulong)Digit(payload[source + i]);
                if (value > Mask32)
                    throw new SamMaskCodecException("Mask payload contains an out-of-range group.");
                source += 5;
                output[destination] = (byte)(value >> 24);
                output[destination + 1] = (byte)(value >> 16);
                output[destination + 2] = (byte)(value >> 8);
                output[destination + 3] = (byte)value;
            }
            if (remainder > 0)
            {
                ulong value = 0;
                for (int i = 0; i < 5; i++)
                    value = value * (ulong)Radix +
                        (ulong)(i < remainder + 1 ? Digit(payload[source + i]) : Radix - 1);
                if (value > Mask32)
                    throw new SamMaskCodecException("Mask payload contains an out-of-range tail.");
                for (int i = 0; i < remainder; i++)
                    output[destination + i] = (byte)(value >> (24 - i * 8));
            }
            if (Pack(output) != payload)
                throw new SamMaskCodecException("Mask payload is not in canonical form.");
            return output;
        }

        // Entry point: validates a wire record's mask and returns a row-major
        // binary raster of exactly maskW * maskH entries (0/1). Lossless '~'
        // coverage bytes binarize at >= 129, matching the reference.
        public static byte[] DecodeToRaster(SamRecord record)
        {
            if (record.maskW <= 0 || record.maskH <= 0)
                throw new SamMaskCodecException("Mask dimensions must be positive safe integers.");
            long area = (long)record.maskW * record.maskH;
            if (area > MaximumRasterArea)
                throw new SamMaskCodecException("Mask dimensions produce an unsafe decoded area.");
            if (string.IsNullOrEmpty(record.maskPayload))
                throw new SamMaskCodecException("Mask payload must be a string.");
            try
            {
                // The grammar parser splits the enc marker off into
                // record.maskEncoding; the reference decoders take the marker
                // as the payload's first character, so it is reattached here.
                if (record.maskEncoding == '~')
                {
                    byte[] coverage = DecodeLosslessRaster(
                        "~" + record.maskPayload, record.maskW, record.maskH);
                    var raster = new byte[coverage.Length];
                    for (int i = 0; i < coverage.Length; i++)
                        raster[i] = coverage[i] >= 129 ? (byte)1 : (byte)0;
                    return raster;
                }
                if (record.maskEncoding == '!')
                {
                    byte[] raster = DecodeOneBit("!" + record.maskPayload, record.maskW, record.maskH);
                    // EncodeRaster returns the marker-prefixed token; the
                    // record stores the payload without the marker.
                    if (EncodeRaster(raster, record.maskW, record.maskH) != "!" + record.maskPayload)
                        throw new SamMaskCodecException(
                            "Mask payload is not canonical or has invalid finalization.");
                    return raster;
                }
            }
            catch (SamMaskCodecException) { throw; }
            catch (Exception e)
            {
                throw new SamMaskCodecException("Mask payload could not be decoded.", e);
            }
            throw new SamMaskCodecException(
                "Unsupported complete mask encoding: " + record.maskEncoding + ".");
        }

        sealed class RangeDecoder
        {
            readonly byte[] input;
            // The constructor consumes the first 4 bytes as the initial code,
            // so live reads continue AFTER them (reference: position = 4).
            int position = 4;
            uint low;
            uint range = Mask32;
            uint code;
            uint scaled;

            public RangeDecoder(byte[] input)
            {
                if (input == null || input.Length < 4)
                    throw new SamMaskCodecException("Mask payload is missing its finalization.");
                this.input = input;
                code = 0;
                for (int i = 0; i < 4; i++)
                    code = unchecked((code << 8) | input[i]);
            }

            uint Read()
            {
                if (position >= input.Length)
                    throw new SamMaskCodecException("Mask payload ended before decoding completed.");
                return input[position++];
            }

            public uint Frequency(uint total)
            {
                scaled = range / total;
                if (scaled == 0)
                    throw new SamMaskCodecException("Mask payload has invalid coding state.");
                uint value = unchecked(code - low) / scaled;
                return value >= total ? total - 1 : value;
            }

            public void Decode(uint cumulative, uint frequency)
            {
                low = unchecked(low + scaled * cumulative);
                range = unchecked(scaled * frequency);
                // Mirrors the reference while-loop exactly: the shift+read
                // body runs when the top bytes match OR after an underflow
                // range assignment - never a bare range-only spin.
                while (true)
                {
                    uint sum = unchecked(low + range);
                    bool aligned = unchecked(low ^ sum) < Top;
                    if (!aligned && range < Bottom)
                        range = unchecked(0u - low) & (Bottom - 1);
                    else if (!aligned)
                        break;
                    code = unchecked((code << 8) | Read());
                    low = unchecked(low << 8);
                    range = unchecked(range << 8);
                }
            }
        }

        sealed class RangeEncoder
        {
            uint low;
            uint range = Mask32;
            readonly List<byte> output = new List<byte>();

            public void Encode(uint cumulative, uint frequency, uint total)
            {
                uint scaled = range / total;
                low = unchecked(low + scaled * cumulative);
                range = unchecked(scaled * frequency);
                // Mirrors the reference while-loop exactly: the emit+shift
                // body runs when the top bytes match OR after an underflow
                // range assignment - never a bare range-only spin.
                while (true)
                {
                    uint sum = unchecked(low + range);
                    bool aligned = unchecked(low ^ sum) < Top;
                    if (!aligned && range < Bottom)
                        range = unchecked(0u - low) & (Bottom - 1);
                    else if (!aligned)
                        break;
                    output.Add((byte)(low >> 24));
                    low = unchecked(low << 8);
                    range = unchecked(range << 8);
                }
            }

            public byte[] Finish()
            {
                for (int i = 0; i < 4; i++)
                {
                    output.Add((byte)(low >> 24));
                    low = unchecked(low << 8);
                }
                return output.ToArray();
            }
        }

        const int Increment = 14;
        const int CountLimit = 4096;
        const int ContextCount = 1 << 12;
        const int Padding = 2;
        const int Symbols = 256;
        const int TotalIndex = 256;
        const int SpatialModes = 5;
        const int ZeroIncrement = 32;
        const int ZeroLimit = 16384;
        const int OrderOneIncrement = 56;
        const int OrderOneLimit = 8192;
        const int OrderTwoStep = 14 * 44;
        const int OrderTwoLimit = 14 * 3584;
        const int OrderZeroStep = 2 * 16;
        const int OrderZeroLimit = 2 * 2048;
        const int OrderZeroInitial = 2;
        const int OrderOneInitial = 1;

        static int ZeroContext(byte[] residuals, int offset, int x, int y, int width)
        {
            int left = x > 0 ? (residuals[offset - 1] == 0 ? 1 : 0) : 1;
            int up = y > 0 ? (residuals[offset - width] == 0 ? 1 : 0) : 1;
            int upperLeft = (x > 0 && y > 0) ? (residuals[offset - width - 1] == 0 ? 1 : 0) : 1;
            int upperRight = (y > 0 && x < width - 1) ? (residuals[offset - width + 1] == 0 ? 1 : 0) : 1;
            return left | (up << 1) | (upperLeft << 2) | (upperRight << 3);
        }

        static int Paeth(int left, int up, int upperLeft)
        {
            int estimate = left + up - upperLeft;
            int leftDistance = Math.Abs(estimate - left);
            int upDistance = Math.Abs(estimate - up);
            int upperLeftDistance = Math.Abs(estimate - upperLeft);
            if (leftDistance <= upDistance && leftDistance <= upperLeftDistance)
                return left;
            return upDistance <= upperLeftDistance ? up : upperLeft;
        }

        static int PredictByte(byte[] output, int offset, int x, int y, int mode, int width)
        {
            if (mode < 0 || mode >= SpatialModes)
                throw new SamMaskCodecException("Lossless mask payload uses an invalid predictor.");
            int left = x > 0 ? output[offset - 1] : 0;
            int up = y > 0 ? output[offset - width] : 0;
            if (mode == 1) return up;
            if (mode == 2) return left;
            if (mode == 3) return (left + up) >> 1;
            int upperLeft = (x > 0 && y > 0) ? output[offset - width - 1] : 0;
            if (mode == 0) return Paeth(left, up, upperLeft);
            if (upperLeft >= Math.Max(left, up)) return Math.Min(left, up);
            if (upperLeft <= Math.Min(left, up)) return Math.Max(left, up);
            return left + up - upperLeft;
        }

        static byte Unzig(int value, int prediction)
        {
            int delta = (value & 1) != 0 ? -((value + 1) >> 1) : (value >> 1);
            return (byte)((prediction + delta) & 0xff);
        }

        static ushort[] GetOrderTwo(Dictionary<int, ushort[]> map, int key)
        {
            ushort[] counts;
            if (!map.TryGetValue(key, out counts))
            {
                counts = new ushort[Symbols + 1];
                map[key] = counts;
            }
            return counts;
        }

        static uint HalveMinOne(uint value)
        {
            uint halved = value >> 1;
            return halved == 0 ? 1u : halved;
        }

        static ushort HalveU16MinOne(ushort value)
        {
            ushort halved = (ushort)(value >> 1);
            return halved == 0 ? (ushort)1 : halved;
        }

        static void UpdateCounts(ushort[] counts, int offset, int bit)
        {
            int slot = offset + bit;
            counts[slot] = unchecked((ushort)(counts[slot] + Increment));
            if (counts[offset] + counts[offset + 1] >= CountLimit)
            {
                counts[offset] = HalveU16MinOne(counts[offset]);
                counts[offset + 1] = HalveU16MinOne(counts[offset + 1]);
            }
        }

        static int ContextAt(byte[] raster, int position, int prior, int earlier)
        {
            return raster[position - 1]
                | (raster[position - 2] << 1)
                | (raster[prior - 2] << 2)
                | (raster[prior - 1] << 3)
                | (raster[prior] << 4)
                | (raster[prior + 1] << 5)
                | (raster[prior + 2] << 6)
                | (raster[earlier - 2] << 7)
                | (raster[earlier - 1] << 8)
                | (raster[earlier] << 9)
                | (raster[earlier + 1] << 10)
                | (raster[earlier + 2] << 11);
        }

        // Reference decodeRaster: adaptive 12-bit-context binary plane.
        static byte[] DecodeOneBit(string payload, int width, int height)
        {
            var decoder = new RangeDecoder(Unpack(payload.Substring(1)));
            var counts = new ushort[ContextCount * 2];
            for (int i = 0; i < counts.Length; i++) counts[i] = 1;
            int paddedWidth = width + Padding * 2;
            var padded = new byte[paddedWidth * (height + Padding)];
            var output = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int row = (y + Padding) * paddedWidth + Padding;
                for (int x = 0; x < width; x++)
                {
                    int position = row + x;
                    int context = ContextAt(padded, position, position - paddedWidth,
                        position - 2 * paddedWidth);
                    int offset = context << 1;
                    uint zero = counts[offset];
                    uint one = counts[offset + 1];
                    uint value = decoder.Frequency(zero + one);
                    int bit = value < zero ? 0 : 1;
                    decoder.Decode(bit == 0 ? 0u : zero, bit == 0 ? zero : one);
                    padded[position] = (byte)bit;
                    output[y * width + x] = (byte)bit;
                    UpdateCounts(counts, offset, bit);
                }
            }
            return output;
        }

        // Reference decodeLosslessRaster: predictor residuals via mixed
        // zero-bit + order-0/1/2 adaptive contexts. Returns coverage bytes;
        // the caller binarizes at >= 129.
        static byte[] DecodeLosslessRaster(string payload, int width, int height)
        {
            if (string.IsNullOrEmpty(payload) || payload[0] != '~')
                throw new SamMaskCodecException("lossless mask payloads must start with ~.");
            byte[] packed = Unpack(payload.Substring(1));
            if (packed.Length < 5)
                throw new SamMaskCodecException("Lossless mask payload is truncated.");
            int selector = packed[0];
            var body = new byte[packed.Length - 1];
            Array.Copy(packed, 1, body, 0, body.Length);
            var decoder = new RangeDecoder(body);
            int length = width * height;
            var output = new byte[length];
            var residuals = new byte[length];
            var zeroCounts = new uint[32];
            for (int i = 0; i < zeroCounts.Length; i++) zeroCounts[i] = 1;
            var orderOne = new ushort[Symbols * Symbols];
            for (int i = 0; i < orderOne.Length; i++) orderOne[i] = OrderOneInitial;
            var orderOneTotals = new int[Symbols];
            for (int i = 0; i < orderOneTotals.Length; i++)
                orderOneTotals[i] = Symbols * OrderOneInitial;
            var orderZero = new ushort[Symbols];
            for (int i = 0; i < orderZero.Length; i++) orderZero[i] = OrderZeroInitial;
            int orderZeroTotal = Symbols * OrderZeroInitial;
            var orderTwo = new Dictionary<int, ushort[]>();
            int previous = 0;
            for (int offset = 0; offset < length; offset++)
            {
                int x = offset % width;
                int y = offset / width;
                int zeroOffset = ZeroContext(residuals, offset, x, y, width) << 1;
                uint zero = zeroCounts[zeroOffset];
                uint nonzero = zeroCounts[zeroOffset + 1];
                uint freq = decoder.Frequency(zero + nonzero);
                int bit = freq < zero ? 0 : 1;
                decoder.Decode(bit == 0 ? 0u : zero, bit == 0 ? zero : nonzero);
                zeroCounts[zeroOffset + bit] = zeroCounts[zeroOffset + bit] + ZeroIncrement;
                if (zeroCounts[zeroOffset] + zeroCounts[zeroOffset + 1] >= ZeroLimit)
                {
                    zeroCounts[zeroOffset] = HalveMinOne(zeroCounts[zeroOffset]);
                    zeroCounts[zeroOffset + 1] = HalveMinOne(zeroCounts[zeroOffset + 1]);
                }
                int symbol = 0;
                if (bit == 1)
                {
                    int above = offset >= width ? residuals[offset - width] : 0;
                    ushort[] second = GetOrderTwo(orderTwo, (previous << 8) | above);
                    int firstOffset = previous << 8;
                    int excludedZero = second[0] + orderOne[firstOffset] + orderZero[0];
                    int total = second[TotalIndex] + orderOneTotals[previous] +
                        orderZeroTotal - excludedZero;
                    uint target = decoder.Frequency((uint)total);
                    int cumulative = 0;
                    symbol = 1;
                    int frequency = second[1] + orderOne[firstOffset + 1] + orderZero[1];
                    while ((long)cumulative + frequency <= target)
                    {
                        cumulative += frequency;
                        symbol++;
                        if (symbol >= Symbols)
                            throw new SamMaskCodecException("Lossless mask payload is malformed.");
                        frequency = second[symbol] + orderOne[firstOffset + symbol] +
                            orderZero[symbol];
                    }
                    decoder.Decode((uint)cumulative, (uint)frequency);
                    second[symbol] = unchecked((ushort)(second[symbol] + OrderTwoStep));
                    second[TotalIndex] = unchecked((ushort)(second[TotalIndex] + OrderTwoStep));
                    if (second[TotalIndex] >= OrderTwoLimit)
                    {
                        int scaledTotal = 0;
                        for (int v = 0; v < Symbols; v++)
                        {
                            second[v] = (ushort)(second[v] >> 1);
                            scaledTotal += second[v];
                        }
                        second[TotalIndex] = unchecked((ushort)scaledTotal);
                    }
                    orderOne[firstOffset + symbol] =
                        unchecked((ushort)(orderOne[firstOffset + symbol] + OrderOneIncrement));
                    orderOneTotals[previous] = orderOneTotals[previous] + OrderOneIncrement;
                    if (orderOneTotals[previous] >= OrderOneLimit)
                    {
                        int scaledTotal = 0;
                        for (int v = 0; v < Symbols; v++)
                        {
                            int scaled = orderOne[firstOffset + v] >> 1;
                            if (scaled == 0) scaled = 1;
                            orderOne[firstOffset + v] = (ushort)scaled;
                            scaledTotal += scaled;
                        }
                        orderOneTotals[previous] = scaledTotal;
                    }
                    orderZero[symbol] = unchecked((ushort)(orderZero[symbol] + OrderZeroStep));
                    orderZeroTotal += OrderZeroStep;
                    if (orderZeroTotal >= OrderZeroLimit)
                    {
                        int scaledTotal = 0;
                        for (int v = 0; v < Symbols; v++)
                        {
                            orderZero[v] = (ushort)(orderZero[v] >> 1);
                            scaledTotal += orderZero[v];
                        }
                        orderZeroTotal = scaledTotal;
                    }
                }
                residuals[offset] = (byte)symbol;
                output[offset] = Unzig(symbol, PredictByte(output, offset, x, y, selector, width));
                previous = symbol;
            }
            return output;
        }

        // Reference encodeRaster: binary-plane encoder. Used for the one_bit
        // canonical check and for synthesizing deterministic test vectors.
        public static string EncodeRaster(byte[] raster, int width, int height)
        {
            if (raster == null || width <= 0 || height <= 0 || raster.Length != width * height)
                throw new SamMaskCodecException("Mask raster dimensions do not match for encoding.");
            var encoder = new RangeEncoder();
            var counts = new ushort[ContextCount * 2];
            for (int i = 0; i < counts.Length; i++) counts[i] = 1;
            int paddedWidth = width + Padding * 2;
            var padded = new byte[paddedWidth * (height + Padding)];
            for (int y = 0; y < height; y++)
            {
                int row = (y + Padding) * paddedWidth + Padding;
                for (int x = 0; x < width; x++)
                {
                    int position = row + x;
                    int context = ContextAt(padded, position, position - paddedWidth,
                        position - 2 * paddedWidth);
                    int offset = context << 1;
                    uint zero = counts[offset];
                    uint one = counts[offset + 1];
                    int bit = raster[y * width + x];
                    if (bit != 0 && bit != 1)
                        throw new SamMaskCodecException("Decoded mask is not binary.");
                    if (bit == 0) encoder.Encode(0, zero, zero + one);
                    else encoder.Encode(zero, one, zero + one);
                    padded[position] = (byte)bit;
                    UpdateCounts(counts, offset, bit);
                }
            }
            return "!" + Pack(encoder.Finish());
        }
    }
}

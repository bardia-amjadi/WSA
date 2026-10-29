namespace WSA.Core.Services;

/// <summary>
/// Splits an H.264 Annex B byte stream into access units (one video frame each, with any SPS/PPS/SEI that precede it),
/// and reads the picture size from an SPS. Phones' encoders emit one slice per frame, so a frame ends at its VCL NAL unit.
/// </summary>
public sealed class H264Splitter
{
    private readonly List<byte> _buffer = new();
    private readonly List<byte[]> _pending = new();
    private bool _started;

    public byte[]? LastSps { get; private set; }

    /// <summary>Adds bytes from the network and returns every frame that is now complete.</summary>
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _buffer.Add(b);
        }

        var frames = new List<byte[]>();
        while (true)
        {
            var first = IndexOfStartCode(_buffer, 0);
            if (first < 0)
            {
                // Everything before the first start code (device name, codec header, dummy byte) is not video.
                if (!_started && _buffer.Count > 4096)
                {
                    _buffer.Clear();
                }

                break;
            }

            var next = IndexOfStartCode(_buffer, first + 3);
            if (next < 0)
            {
                // The last NAL unit may still be arriving.
                if (first > 0)
                {
                    _buffer.RemoveRange(0, first);
                }

                break;
            }

            // Include a leading zero of a 4-byte start code in the previous NAL's end trimming.
            var end = next > 0 && _buffer[next - 1] == 0 ? next - 1 : next;
            var nal = _buffer.GetRange(first, end - first).ToArray();
            _buffer.RemoveRange(0, end);
            _started = true;
            AddNal(nal, frames);
        }

        return frames;
    }

    private void AddNal(byte[] nal, List<byte[]> frames)
    {
        var type = NalType(nal);
        if (type == 7)
        {
            LastSps = nal;
        }

        _pending.Add(nal);
        if (type is 1 or 5)
        {
            var total = _pending.Sum(n => n.Length);
            var frame = new byte[total];
            var offset = 0;
            foreach (var part in _pending)
            {
                Buffer.BlockCopy(part, 0, frame, offset, part.Length);
                offset += part.Length;
            }

            _pending.Clear();
            frames.Add(frame);
        }
    }

    /// <summary>True if the access unit contains an IDR slice (a frame the decoder can start from).</summary>
    public static bool IsKeyFrame(byte[] frame)
    {
        for (var i = 0; i + 3 < frame.Length; i++)
        {
            if (frame[i] == 0 && frame[i + 1] == 0 && frame[i + 2] == 1 && (frame[i + 3] & 0x1F) == 5)
            {
                return true;
            }
        }

        return false;
    }

    public static int NalType(byte[] nal)
    {
        var i = nal.Length > 3 && nal[2] == 1 ? 3 : 4;
        return i < nal.Length ? nal[i] & 0x1F : -1;
    }

    private static int IndexOfStartCode(List<byte> data, int from)
    {
        for (var i = from; i + 2 < data.Count; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Reads width and height (after cropping) from a sequence parameter set NAL unit. Returns false if it uses features this reader skips.</summary>
    public static bool TryReadSize(byte[] sps, out int width, out int height)
    {
        width = height = 0;
        try
        {
            var start = sps.Length > 3 && sps[2] == 1 ? 3 : 4;
            var rbsp = new List<byte>();
            for (var i = start + 1; i < sps.Length; i++)
            {
                // Remove emulation prevention bytes (00 00 03).
                if (i >= 2 && sps[i] == 3 && sps[i - 1] == 0 && sps[i - 2] == 0)
                {
                    continue;
                }

                rbsp.Add(sps[i]);
            }

            var r = new BitReader(rbsp.ToArray());
            var profile = r.Bits(8);
            r.Bits(8);
            r.Bits(8);
            r.Ue();
            var chroma = 1;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chroma = r.Ue();
                if (chroma == 3)
                {
                    r.Bits(1);
                }

                r.Ue();
                r.Ue();
                r.Bits(1);
                if (r.Bits(1) == 1)
                {
                    return false; // scaling matrices: not read
                }
            }

            r.Ue();
            var pocType = r.Ue();
            if (pocType == 0)
            {
                r.Ue();
            }
            else if (pocType == 1)
            {
                return false;
            }

            r.Ue();
            r.Bits(1);
            var widthMbs = r.Ue() + 1;
            var heightMapUnits = r.Ue() + 1;
            var frameMbsOnly = r.Bits(1);
            if (frameMbsOnly == 0)
            {
                r.Bits(1);
            }

            r.Bits(1);
            var cropLeft = 0;
            var cropRight = 0;
            var cropTop = 0;
            var cropBottom = 0;
            if (r.Bits(1) == 1)
            {
                cropLeft = r.Ue();
                cropRight = r.Ue();
                cropTop = r.Ue();
                cropBottom = r.Ue();
            }

            var cropUnitX = chroma == 0 ? 1 : chroma == 3 ? 1 : 2;
            var cropUnitY = (chroma == 0 ? 1 : chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
            width = widthMbs * 16 - (cropLeft + cropRight) * cropUnitX;
            height = (2 - frameMbsOnly) * heightMapUnits * 16 - (cropTop + cropBottom) * cropUnitY;
            return width > 0 && height > 0;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _pos;

        public BitReader(byte[] data) => _data = data;

        public int Bits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var bit = (_data[_pos >> 3] >> (7 - (_pos & 7))) & 1;
                value = (value << 1) | bit;
                _pos++;
            }

            return value;
        }

        public int Ue()
        {
            var zeros = 0;
            while (Bits(1) == 0)
            {
                zeros++;
                if (zeros > 31)
                {
                    throw new IndexOutOfRangeException();
                }
            }

            return zeros == 0 ? 0 : (1 << zeros) - 1 + Bits(zeros);
        }
    }
}

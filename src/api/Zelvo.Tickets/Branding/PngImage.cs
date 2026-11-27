using System.Buffers.Binary;
using System.IO.Compression;

namespace Zelvo.Tickets.Branding;

/// <summary>A PNG that Zelvo cannot read, with the reason for the administrator.</summary>
public sealed class PngFormatException(string code, string message) : Exception(message)
{
    /// <summary>Problem code: <c>png-unreadable</c>, <c>png-interlaced</c>, <c>png-too-large</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// A picture as 8-bit RGBA pixels (row after row, no padding), read from and written as PNG.
/// Task J1: Zelvo resizes the tenant's app icon itself (no image library), so the reader only
/// takes what an icon needs: non-interlaced PNGs of every colour type and bit depth, palettes
/// with transparency, up to <see cref="MaxSide"/> pixels a side.
/// </summary>
public sealed class PngImage
{
    /// <summary>Largest width or height read (a 4096 × 4096 RGBA picture is 64 MiB unpacked).</summary>
    public const int MaxSide = 4096;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>A transparent picture.</summary>
    public PngImage(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>RGBA bytes, row after row.</summary>
    public byte[] Pixels { get; }

    /// <summary>Width and height from the header, without reading the pixels; null when it is not a PNG.</summary>
    public static (int Width, int Height)? ReadSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || !png[..8].SequenceEqual(Signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>Reads a PNG.</summary>
    /// <exception cref="PngFormatException">The file is not a PNG Zelvo can read.</exception>
    public static PngImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        var span = png.AsSpan();
        if (span.Length < 8 || !span[..8].SequenceEqual(Signature))
        {
            throw Unreadable();
        }

        int width = 0, height = 0, bitDepth = 0, colourType = -1;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var data = new MemoryStream();
        var position = 8;
        var ended = false;
        while (!ended)
        {
            if (position + 12 > span.Length)
            {
                throw Unreadable();
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(span.Slice(position, 4));
            if (length < 0 || position + 12 + length > span.Length)
            {
                throw Unreadable();
            }

            var type = span.Slice(position + 4, 4);
            var body = span.Slice(position + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(position + 8 + length, 4));
            if (Crc(span.Slice(position + 4, 4 + length)) != crc)
            {
                throw Unreadable();
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13)
                {
                    throw Unreadable();
                }

                width = BinaryPrimitives.ReadInt32BigEndian(body[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(body.Slice(4, 4));
                bitDepth = body[8];
                colourType = body[9];
                if (body[10] != 0 || body[11] != 0)
                {
                    throw Unreadable();
                }

                if (body[12] != 0)
                {
                    throw new PngFormatException("png-interlaced", "Save the image without interlacing (Adam7) and upload it again.");
                }

                if (width < 1 || height < 1)
                {
                    throw Unreadable();
                }

                if (width > MaxSide || height > MaxSide)
                {
                    throw new PngFormatException("png-too-large", $"The image can be at most {MaxSide} × {MaxSide} pixels.");
                }

                if (!Supported(colourType, bitDepth))
                {
                    throw Unreadable();
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = body.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = body.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (colourType < 0)
                {
                    throw Unreadable();
                }

                data.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                ended = true;
            }

            position += 12 + length;
        }

        if (colourType < 0 || data.Length == 0 || (colourType == 3 && (palette is null || palette.Length % 3 != 0)))
        {
            throw Unreadable();
        }

        var channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bitsPerPixel = channels * bitDepth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var raw = Inflate(data.ToArray(), checked((stride + 1) * height));
        var image = new PngImage(width, height);
        Unfilter(raw, stride, height, Math.Max(1, bitsPerPixel / 8));
        image.Fill(raw, stride, colourType, bitDepth, palette, transparency);
        return image;
    }

    /// <summary>Writes the picture as a PNG (RGBA, 8 bits, every row filtered with Paeth).</summary>
    public byte[] Encode()
    {
        var stride = Width * 4;
        var filtered = new byte[(stride + 1) * Height];
        for (var y = 0; y < Height; y++)
        {
            var row = y * (stride + 1);
            filtered[row] = 4;
            for (var x = 0; x < stride; x++)
            {
                var value = Pixels[y * stride + x];
                var left = x >= 4 ? Pixels[y * stride + x - 4] : 0;
                var up = y > 0 ? Pixels[(y - 1) * stride + x] : 0;
                var upLeft = x >= 4 && y > 0 ? Pixels[(y - 1) * stride + x - 4] : 0;
                filtered[row + 1 + x] = (byte)(value - Paeth(left, up, upLeft));
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(filtered);
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], Width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>
    /// The picture scaled to <paramref name="width"/> × <paramref name="height"/>: each new pixel is
    /// the average of the area it covers (with alpha weighting, so transparent edges keep no
    /// colour fringe), which keeps logos sharp when they are made smaller.
    /// </summary>
    public PngImage Resize(int width, int height)
    {
        var horizontal = Weights(Width, width);
        var vertical = Weights(Height, height);

        // Premultiplied RGBA as doubles, scaled across first, then down.
        var across = new double[Height * width * 4];
        Span<double> sum = stackalloc double[4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                sum.Clear();
                foreach (var (source, weight) in horizontal[x])
                {
                    var i = (y * Width + source) * 4;
                    var alpha = Pixels[i + 3] / 255.0;
                    sum[0] += Pixels[i] * alpha * weight;
                    sum[1] += Pixels[i + 1] * alpha * weight;
                    sum[2] += Pixels[i + 2] * alpha * weight;
                    sum[3] += alpha * weight;
                }

                var o = (y * width + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    across[o + c] = sum[c];
                }
            }
        }

        var result = new PngImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                sum.Clear();
                foreach (var (source, weight) in vertical[y])
                {
                    var i = (source * width + x) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        sum[c] += across[i + c] * weight;
                    }
                }

                var o = (y * width + x) * 4;
                var a = sum[3];
                if (a <= 0)
                {
                    continue;
                }

                result.Pixels[o] = ToByte(sum[0] / a);
                result.Pixels[o + 1] = ToByte(sum[1] / a);
                result.Pixels[o + 2] = ToByte(sum[2] / a);
                result.Pixels[o + 3] = ToByte(a * 255);
            }
        }

        return result;
    }

    /// <summary>A square of <paramref name="size"/> pixels filled with <paramref name="background"/> and this picture scaled to <paramref name="share"/> of it, centred.</summary>
    public PngImage OnBackground(int size, (byte R, byte G, byte B) background, double share)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(share, 0.1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(share, 1.0);
        var result = new PngImage(size, size);
        for (var i = 0; i < result.Pixels.Length; i += 4)
        {
            result.Pixels[i] = background.R;
            result.Pixels[i + 1] = background.G;
            result.Pixels[i + 2] = background.B;
            result.Pixels[i + 3] = 255;
        }

        var inner = Math.Max(1, (int)Math.Round(size * share));
        var scaled = Resize(inner, Math.Max(1, (int)Math.Round(inner * (double)Height / Width)));
        var left = (size - scaled.Width) / 2;
        var top = (size - scaled.Height) / 2;
        for (var y = 0; y < scaled.Height; y++)
        {
            for (var x = 0; x < scaled.Width; x++)
            {
                var s = (y * scaled.Width + x) * 4;
                var d = ((top + y) * size + left + x) * 4;
                var alpha = scaled.Pixels[s + 3] / 255.0;
                for (var c = 0; c < 3; c++)
                {
                    result.Pixels[d + c] = ToByte(scaled.Pixels[s + c] * alpha + result.Pixels[d + c] * (1 - alpha));
                }
            }
        }

        return result;
    }

    /// <summary>The colour of the top-left pixel when it is fully opaque (the icon's own background), else null.</summary>
    public (byte R, byte G, byte B)? OpaqueCorner() =>
        Pixels[3] == 255 ? (Pixels[0], Pixels[1], Pixels[2]) : null;

    private static bool Supported(int colourType, int bitDepth) => colourType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        2 or 4 or 6 => bitDepth is 8 or 16,
        _ => false,
    };

    private static PngFormatException Unreadable() => new("png-unreadable", "The file is not a PNG image Zelvo can read.");

    private static byte[] Inflate(byte[] compressed, int expected)
    {
        var raw = new byte[expected];
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var read = 0;
            while (read < expected)
            {
                var n = zlib.Read(raw, read, expected - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read < expected)
            {
                throw Unreadable();
            }
        }
        catch (InvalidDataException)
        {
            throw Unreadable();
        }

        return raw;
    }

    private static void Unfilter(byte[] raw, int stride, int height, int bytesPerPixel)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            var previous = row - stride - 1;
            var filter = raw[row];
            for (var x = 0; x < stride; x++)
            {
                var i = row + 1 + x;
                int left = x >= bytesPerPixel ? raw[i - bytesPerPixel] : 0;
                int up = y > 0 ? raw[previous + 1 + x] : 0;
                int upLeft = x >= bytesPerPixel && y > 0 ? raw[previous + 1 + x - bytesPerPixel] : 0;
                raw[i] = filter switch
                {
                    0 => raw[i],
                    1 => (byte)(raw[i] + left),
                    2 => (byte)(raw[i] + up),
                    3 => (byte)(raw[i] + ((left + up) >> 1)),
                    4 => (byte)(raw[i] + Paeth(left, up, upLeft)),
                    _ => throw Unreadable(),
                };
            }
        }
    }

    private void Fill(byte[] raw, int stride, int colourType, int bitDepth, byte[]? palette, byte[]? transparency)
    {
        var maxSample = (1 << bitDepth) - 1;
        for (var y = 0; y < Height; y++)
        {
            var row = y * (stride + 1) + 1;
            for (var x = 0; x < Width; x++)
            {
                var o = (y * Width + x) * 4;
                switch (colourType)
                {
                    case 0:
                    {
                        var sample = Sample(raw, row, x, bitDepth);
                        var grey = (byte)(sample * 255 / maxSample);
                        Pixels[o] = Pixels[o + 1] = Pixels[o + 2] = grey;
                        var key = transparency is { Length: >= 2 } ? BinaryPrimitives.ReadUInt16BigEndian(transparency) : -1;
                        Pixels[o + 3] = sample == key ? (byte)0 : (byte)255;
                        break;
                    }

                    case 3:
                    {
                        var index = Sample(raw, row, x, bitDepth);
                        if (index * 3 + 2 >= palette!.Length)
                        {
                            throw Unreadable();
                        }

                        Pixels[o] = palette[index * 3];
                        Pixels[o + 1] = palette[index * 3 + 1];
                        Pixels[o + 2] = palette[index * 3 + 2];
                        Pixels[o + 3] = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
                        break;
                    }

                    default:
                    {
                        var channels = colourType switch { 2 => 3, 4 => 2, _ => 4 };
                        var bytes = bitDepth / 8;
                        var start = row + x * channels * bytes;
                        byte Channel(int c) => raw[start + c * bytes];
                        if (colourType == 4)
                        {
                            Pixels[o] = Pixels[o + 1] = Pixels[o + 2] = Channel(0);
                            Pixels[o + 3] = Channel(1);
                        }
                        else
                        {
                            Pixels[o] = Channel(0);
                            Pixels[o + 1] = Channel(1);
                            Pixels[o + 2] = Channel(2);
                            Pixels[o + 3] = colourType == 6 ? Channel(3) : (byte)255;
                            if (colourType == 2 && transparency is { Length: >= 6 } && bytes == 1
                                && transparency[1] == Pixels[o] && transparency[3] == Pixels[o + 1] && transparency[5] == Pixels[o + 2])
                            {
                                Pixels[o + 3] = 0;
                            }
                        }

                        break;
                    }
                }
            }
        }
    }

    private static int Sample(byte[] raw, int row, int x, int bitDepth)
    {
        if (bitDepth == 16)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(row + x * 2, 2));
        }

        var bit = x * bitDepth;
        var value = raw[row + bit / 8];
        var shift = 8 - bitDepth - bit % 8;
        return (value >> shift) & ((1 << bitDepth) - 1);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    // CRC-32 of PNG chunks (ISO 3309, as in the PNG specification).
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var c = 0xffffffffu;
        foreach (var b in bytes)
        {
            c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        }

        return c ^ 0xffffffffu;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    // For each target index, the source indexes it covers with the share of each (area average).
    private static List<(int Source, double Weight)>[] Weights(int from, int to)
    {
        var scale = (double)from / to;
        var weights = new List<(int, double)>[to];
        for (var i = 0; i < to; i++)
        {
            var start = i * scale;
            var end = Math.Min(from, (i + 1) * scale);
            var list = new List<(int, double)>();
            if (scale <= 1)
            {
                list.Add((Math.Min(from - 1, (int)Math.Floor((start + end) / 2)), 1));
            }
            else
            {
                for (var j = (int)Math.Floor(start); j < Math.Ceiling(end); j++)
                {
                    var overlap = Math.Min(end, j + 1) - Math.Max(start, j);
                    if (overlap > 0)
                    {
                        list.Add((j, overlap / scale));
                    }
                }
            }

            weights[i] = list;
        }

        return weights;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);
        var crcInput = new byte[4 + body.Length];
        type.CopyTo(crcInput);
        body.CopyTo(crcInput.AsSpan(4));
        output.Write(crcInput);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(crcInput));
        output.Write(crc);
    }
}

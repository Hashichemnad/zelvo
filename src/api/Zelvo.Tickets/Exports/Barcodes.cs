using System.Globalization;
using System.Text;

namespace Zelvo.Tickets.Exports;

/// <summary>
/// Barcodes for asset labels (task G3; product owner decision 16 of 2026-11-26: labels carry the
/// asset tag only, as a QR code and as Code 128). Zelvo's own small encoders, drawn as SVG for the
/// PDF renderer, which runs no scripts: QR codes in byte mode with error correction level M,
/// versions 1 to 3 (up to 42 bytes; asset tags have at most 32), and Code 128 with code set B.
/// </summary>
public static class Barcodes
{
    /// <summary>The longest text a QR code here holds.</summary>
    public const int MaxQrBytes = 42;

    // Version → (size, data codewords, error correction codewords, alignment centre or 0), level M, one block.
    private static readonly (int Size, int Data, int Ec, int Align)[] Versions = [(21, 16, 10, 0), (25, 28, 16, 18), (29, 44, 26, 22)];

    /// <summary>The QR code of <paramref name="text"/> as dark (true) and light modules, row by row.</summary>
    public static bool[,] QrMatrix(string text, int? forceMask = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var version = Array.FindIndex(Versions, v => (v.Data * 8 - 12) / 8 >= bytes.Length);
        if (version < 0)
        {
            throw new ArgumentException($"At most {MaxQrBytes} bytes fit in these QR codes.", nameof(text));
        }

        var (size, dataLength, ecLength, align) = Versions[version];

        // Byte mode, an 8-bit count, the bytes, the terminator, then padding.
        var bits = new List<bool>();
        void Put(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) == 1);
            }
        }

        Put(0b0100, 4);
        Put(bytes.Length, 8);
        foreach (var b in bytes)
        {
            Put(b, 8);
        }

        Put(0, Math.Min(4, dataLength * 8 - bits.Count));
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        var data = new byte[dataLength];
        for (var i = 0; i < bits.Count / 8; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                data[i] = (byte)((data[i] << 1) | (bits[i * 8 + j] ? 1 : 0));
            }
        }

        for (int i = bits.Count / 8, pad = 0; i < dataLength; i++, pad++)
        {
            data[i] = pad % 2 == 0 ? (byte)0xEC : (byte)0x11;
        }

        var codewords = data.Concat(ReedSolomon(data, ecLength)).ToArray();

        // Function patterns.
        var modules = new bool[size, size];
        var function = new bool[size, size];
        void Set(int row, int column, bool dark)
        {
            modules[row, column] = dark;
            function[row, column] = true;
        }

        for (var i = 0; i < size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }

        foreach (var (row, column) in new[] { (3, 3), (3, size - 4), (size - 4, 3) })
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var r = row + dy;
                    var c = column + dx;
                    if (r >= 0 && r < size && c >= 0 && c < size)
                    {
                        var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        Set(r, c, distance != 2 && distance != 4);
                    }
                }
            }
        }

        if (align > 0)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    Set(align + dy, align + dx, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }
        }

        // Format areas are reserved now and written once the mask is chosen.
        DrawFormat(Set, size, 0);

        // Data, two columns at a time from the right, up and down in turn, around the timing column.
        var index = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (var step = 0; step < size; step++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var column = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var row = upward ? size - 1 - step : step;
                    if (!function[row, column] && index < codewords.Length * 8)
                    {
                        modules[row, column] = ((codewords[index >> 3] >> (7 - (index & 7))) & 1) == 1;
                        index++;
                    }
                }
            }
        }

        // The mask with the lowest penalty (any decodes; the best scans most easily).
        var best = forceMask ?? Enumerable.Range(0, 8).MinBy(mask =>
        {
            var trial = (bool[,])modules.Clone();
            ApplyMask(trial, function, mask);
            DrawFormat((r, c, d) => trial[r, c] = d, size, mask);
            return Penalty(trial);
        });
        ApplyMask(modules, function, best);
        DrawFormat((r, c, d) => modules[r, c] = d, size, best);
        return modules;
    }

    /// <summary>The QR code as an SVG image <paramref name="sizeMm"/> millimetres square, with the four-module quiet zone.</summary>
    public static string QrSvg(string text, double sizeMm)
    {
        var matrix = QrMatrix(text);
        var size = matrix.GetLength(0);
        var path = new StringBuilder();
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                if (matrix[row, column])
                {
                    path.Append(CultureInfo.InvariantCulture, $"M{column + 4} {row + 4}h1v1h-1z");
                }
            }
        }

        var total = size + 8;
        return string.Create(CultureInfo.InvariantCulture,
            $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {total} {total}" width="{sizeMm}mm" height="{sizeMm}mm" shape-rendering="crispEdges" aria-hidden="true"><rect width="{total}" height="{total}" fill="#fff"/><path d="{path}" fill="#000"/></svg>""");
    }

    // Code 128 bar and space widths, values 0 to 106 (106: stop, with its final bar).
    private static readonly string[] Code128Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", "221312", "231212", "112232", "122132", "122231",
        "113222", "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313", "231113", "231311", "112133", "112331", "132131",
        "113123", "113321", "133121", "313121", "211331", "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214", "112412", "122114", "122411", "142112", "142211",
        "241211", "221114", "413111", "241112", "134111", "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412", "211214",
        "211232", "2331112",
    ];

    /// <summary>The Code 128 (code set B) symbol of <paramref name="text"/>: start, characters, check, stop.</summary>
    public static IReadOnlyList<int> Code128Values(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Any(c => c is < ' ' or > '~'))
        {
            throw new ArgumentException("Code 128 here takes printable ASCII characters.", nameof(text));
        }

        var values = new List<int> { 104 };
        values.AddRange(text.Select(c => c - 32));
        var check = 104;
        for (var i = 1; i < values.Count; i++)
        {
            check += values[i] * i;
        }

        values.Add(check % 103);
        values.Add(106);
        return values;
    }

    /// <summary>Code 128 as an SVG image <paramref name="widthMm"/> by <paramref name="heightMm"/> millimetres, with a 10-module quiet zone each side.</summary>
    public static string Code128Svg(string text, double widthMm, double heightMm)
    {
        var bars = new StringBuilder();
        var x = 10;
        foreach (var value in Code128Values(text))
        {
            var pattern = Code128Patterns[value];
            for (var i = 0; i < pattern.Length; i++)
            {
                var width = pattern[i] - '0';
                if (i % 2 == 0)
                {
                    bars.Append(CultureInfo.InvariantCulture, $"M{x} 0h{width}v1h-{width}z");
                }

                x += width;
            }
        }

        var total = x + 10;
        return string.Create(CultureInfo.InvariantCulture,
            $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {total} 1" preserveAspectRatio="none" width="{widthMm}mm" height="{heightMm}mm" shape-rendering="crispEdges" aria-hidden="true"><rect width="{total}" height="1" fill="#fff"/><path d="{bars}" fill="#000"/></svg>""");
    }

    /// <summary>The modules of a Code 128 symbol as bar widths (for tests): bar, space, bar….</summary>
    public static string Code128Widths(string text) => string.Concat(Code128Values(text).Select(v => Code128Patterns[v]));

    private static byte[] ReedSolomon(byte[] data, int degree)
    {
        // The generator polynomial, product of (x - 2^i) for i below degree, highest term left out.
        var divisor = new byte[degree];
        divisor[^1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                divisor[j] = Multiply(divisor[j], root);
                if (j + 1 < degree)
                {
                    divisor[j] ^= divisor[j + 1];
                }
            }

            root = Multiply(root, 2);
        }

        var remainder = new byte[degree];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, degree - 1);
            remainder[^1] = 0;
            for (var i = 0; i < degree; i++)
            {
                remainder[i] ^= Multiply(divisor[i], factor);
            }
        }

        return remainder;
    }

    private static byte Multiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private static void DrawFormat(Action<int, int, bool> set, int size, int mask)
    {
        // Level M (00) and the mask, with the BCH (15, 5) remainder, XOR 101010000010010.
        var data = mask;
        var remainder = data;
        for (var i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        var bits = ((data << 10) | remainder) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) == 1;
        for (var i = 0; i <= 5; i++)
        {
            set(i, 8, Bit(i));
        }

        set(7, 8, Bit(6));
        set(8, 8, Bit(7));
        set(8, 7, Bit(8));
        for (var i = 9; i < 15; i++)
        {
            set(8, 14 - i, Bit(i));
        }

        for (var i = 0; i < 8; i++)
        {
            set(8, size - 1 - i, Bit(i));
        }

        for (var i = 8; i < 15; i++)
        {
            set(size - 15 + i, 8, Bit(i));
        }

        set(size - 8, 8, true);
    }

    private static void ApplyMask(bool[,] modules, bool[,] function, int mask)
    {
        var size = modules.GetLength(0);
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                var flip = mask switch
                {
                    0 => (row + column) % 2 == 0,
                    1 => row % 2 == 0,
                    2 => column % 3 == 0,
                    3 => (row + column) % 3 == 0,
                    4 => (row / 2 + column / 3) % 2 == 0,
                    5 => row * column % 2 + row * column % 3 == 0,
                    6 => (row * column % 2 + row * column % 3) % 2 == 0,
                    _ => ((row + column) % 2 + row * column % 3) % 2 == 0,
                };
                if (flip && !function[row, column])
                {
                    modules[row, column] = !modules[row, column];
                }
            }
        }
    }

    private static int Penalty(bool[,] m)
    {
        var size = m.GetLength(0);
        var penalty = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < size; a++)
            {
                var run = 1;
                for (var b = 1; b < size; b++)
                {
                    var same = pass == 0 ? m[a, b] == m[a, b - 1] : m[b, a] == m[b - 1, a];
                    if (same)
                    {
                        run++;
                    }
                    else
                    {
                        penalty += run >= 5 ? run - 2 : 0;
                        run = 1;
                    }
                }

                penalty += run >= 5 ? run - 2 : 0;

                // Patterns that look like a finder: 1011101 with four light modules on one side.
                for (var b = 0; b + 10 < size; b++)
                {
                    bool At(int i) => pass == 0 ? m[a, b + i] : m[b + i, a];
                    var core = At(0) && !At(1) && At(2) && At(3) && At(4) && !At(5) && At(6);
                    var coreAfter = !At(0) && !At(1) && !At(2) && !At(3) && At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
                    if ((core && !At(7) && !At(8) && !At(9) && !At(10)) || coreAfter)
                    {
                        penalty += 40;
                    }
                }
            }
        }

        var dark = 0;
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                dark += m[row, column] ? 1 : 0;
                if (row + 1 < size && column + 1 < size && m[row, column] == m[row, column + 1] && m[row, column] == m[row + 1, column] && m[row, column] == m[row + 1, column + 1])
                {
                    penalty += 3;
                }
            }
        }

        penalty += Math.Abs(dark * 20 - size * size * 10) / (size * size) * 10;
        return penalty;
    }
}

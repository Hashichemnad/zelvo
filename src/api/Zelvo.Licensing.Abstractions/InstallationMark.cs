using System.Text;

namespace Zelvo.Licensing;

/// <summary>The watermark of this installation, for the places generated files carry it.</summary>
public interface IInstallationWatermark
{
    /// <summary>The license's watermark (or the build's), or null in development.</summary>
    string? Current { get; }
}

/// <summary>
/// The hidden mark Zelvo puts in the files it generates (module 12, task F6): PDF exports, Excel
/// exports and emails carry the installation's watermark in an unremarkable place and form, so a
/// leaked file can be traced to the customer it came from. The mark is not the watermark's text
/// (a search for "ZW-" finds nothing): it is the watermark's characters XOR-ed with a fixed key and
/// written as lowercase hexadecimal, which <see cref="Decode"/> (and <c>zelvo-sign mark</c>) turn
/// back. It deters and traces; like every watermark, someone who knows where it is can remove it.
/// </summary>
public static class InstallationMark
{
    /// <summary>The email header that carries the mark (a header bulk mailers commonly send).</summary>
    public const string EmailHeader = "X-Entity-Ref-ID";

    /// <summary>The Excel custom document property that carries the mark.</summary>
    public const string SpreadsheetProperty = "ContentId";

    private static readonly byte[] Key = "zelvo-installation-mark"u8.ToArray();

    /// <summary>The mark for <paramref name="watermark"/>, or null when there is none.</summary>
    public static string? Encode(string? watermark)
    {
        if (string.IsNullOrEmpty(watermark))
        {
            return null;
        }

        var bytes = Encoding.ASCII.GetBytes(watermark);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= Key[i % Key.Length];
        }

        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>The watermark a mark carries, or null when it is not one of ours.</summary>
    public static string? Decode(string? mark)
    {
        if (string.IsNullOrWhiteSpace(mark) || mark.Length % 2 != 0)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(mark.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= Key[i % Key.Length];
        }

        var text = Encoding.ASCII.GetString(bytes);
        return text.StartsWith("ZW-", StringComparison.Ordinal) ? text : null;
    }

    /// <summary>
    /// <paramref name="pdf"/> with the mark as a PDF comment just before the last <c>%%EOF</c>:
    /// readers ignore comments, and the cross-reference offsets before it do not move.
    /// </summary>
    public static byte[] MarkPdf(byte[] pdf, string? watermark)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (Encode(watermark) is not { } mark)
        {
            return pdf;
        }

        var eof = LastIndexOf(pdf, "%%EOF"u8);
        if (eof < 0)
        {
            return pdf;
        }

        var comment = Encoding.ASCII.GetBytes($"%{mark}\n");
        var marked = new byte[pdf.Length + comment.Length];
        pdf.AsSpan(0, eof).CopyTo(marked);
        comment.CopyTo(marked.AsSpan(eof));
        pdf.AsSpan(eof).CopyTo(marked.AsSpan(eof + comment.Length));
        return marked;
    }

    /// <summary>The mark in a PDF made by <see cref="MarkPdf"/>, or null.</summary>
    public static string? ReadPdf(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var eof = LastIndexOf(pdf, "%%EOF"u8);
        if (eof < 2)
        {
            return null;
        }

        var end = eof - 1; // the newline after the mark
        var start = end;
        while (start > 0 && pdf[start - 1] != (byte)'%' && pdf[start - 1] != (byte)'\n')
        {
            start--;
        }

        return start > 0 && pdf[start - 1] == (byte)'%' ? Decode(Encoding.ASCII.GetString(pdf, start, end - start)) : null;
    }

    private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> value) => data.AsSpan().LastIndexOf(value);
}

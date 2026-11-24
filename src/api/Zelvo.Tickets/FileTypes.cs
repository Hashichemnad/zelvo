using System.Text;

namespace Zelvo.Tickets;

/// <summary>A file type tickets can accept: its extension, media type and how its content is checked.</summary>
/// <param name="Extension">Lower-case extension without the dot.</param>
/// <param name="ContentType">Media type sent on download (never the uploader's).</param>
/// <param name="Check">Whether the content looks like this type.</param>
public sealed record FileType(string Extension, string ContentType, Func<ReadOnlyMemory<byte>, bool> Check);

/// <summary>
/// The file types Zelvo knows how to check. Tenants choose which of them to allow
/// (<see cref="AttachmentPolicy"/>). The content must match the extension: a renamed executable
/// is refused before the malware scan even runs.
/// </summary>
public static class FileTypes
{
    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Gif87 = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89 = "GIF89a"u8.ToArray();
    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] Webp = "WEBP"u8.ToArray();
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmpty = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] Ico = [0x00, 0x00, 0x01, 0x00];
    private static readonly byte[] OleCompound = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Every known type, by extension.</summary>
    public static IReadOnlyList<FileType> All { get; } =
    [
        new("pdf", "application/pdf", c => StartsWith(c, Pdf)),
        new("png", "image/png", c => StartsWith(c, Png)),
        new("jpg", "image/jpeg", c => StartsWith(c, Jpeg)),
        new("jpeg", "image/jpeg", c => StartsWith(c, Jpeg)),
        new("gif", "image/gif", c => StartsWith(c, Gif87) || StartsWith(c, Gif89)),
        new("ico", "image/x-icon", c => StartsWith(c, Ico)),
        new("webp", "image/webp", c => StartsWith(c, Riff) && c.Length >= 12 && c.Span[8..12].SequenceEqual(Webp)),
        new("txt", "text/plain; charset=utf-8", IsText),
        new("csv", "text/csv; charset=utf-8", IsText),
        new("log", "text/plain; charset=utf-8", IsText),
        new("eml", "message/rfc822", IsText),
        new("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", IsZip),
        new("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", IsZip),
        new("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", IsZip),
        new("odt", "application/vnd.oasis.opendocument.text", IsZip),
        new("ods", "application/vnd.oasis.opendocument.spreadsheet", IsZip),
        new("zip", "application/zip", IsZip),
        new("msg", "application/vnd.ms-outlook", c => StartsWith(c, OleCompound)),
    ];

    /// <summary>The type with <paramref name="extension"/> (without the dot), or null.</summary>
    public static FileType? Find(string? extension) =>
        extension is null ? null : All.FirstOrDefault(t => string.Equals(t.Extension, extension, StringComparison.Ordinal));

    /// <summary>Lower-case extension of <paramref name="fileName"/> without the dot, or null.</summary>
    public static string? ExtensionOf(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : null;
    }

    /// <summary>
    /// The file name to store and show: no folders, no control or bidirectional-override
    /// characters (which can disguise an extension), at most 255 characters with the extension kept.
    /// </summary>
    public static string CleanFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            if (!char.IsControl(c) && c is not ('‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or '"' or '/' or ':' or '*' or '?' or '<' or '>' or '|'))
            {
                builder.Append(c);
            }
        }

        var cleaned = builder.ToString().Trim().Trim('.');
        if (cleaned.Length > 255)
        {
            var extension = Path.GetExtension(cleaned);
            cleaned = cleaned[..(255 - extension.Length)] + extension;
        }

        return cleaned;
    }

    private static bool StartsWith(ReadOnlyMemory<byte> content, byte[] signature) =>
        content.Length >= signature.Length && content.Span[..signature.Length].SequenceEqual(signature);

    private static bool IsZip(ReadOnlyMemory<byte> content) => StartsWith(content, Zip) || StartsWith(content, ZipEmpty);

    // Text files: valid UTF-8 (with or without a byte order mark) and no NUL characters.
    private static bool IsText(ReadOnlyMemory<byte> content)
    {
        var span = content.Span;
        if (span.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

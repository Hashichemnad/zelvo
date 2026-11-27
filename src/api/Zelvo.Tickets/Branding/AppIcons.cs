namespace Zelvo.Tickets.Branding;

/// <summary>How an icon of the installable web app is drawn.</summary>
public enum AppIconPurpose
{
    /// <summary>The icon as uploaded, transparency kept (manifest purpose <c>any</c>).</summary>
    Any,

    /// <summary>On a full background with the icon inside the safe zone, so round and squircle masks never cut it (manifest purpose <c>maskable</c>).</summary>
    Maskable,

    /// <summary>On a full background, edge to edge (iOS <c>apple-touch-icon</c>, which shows transparency as black).</summary>
    Apple,
}

/// <summary>One size of the app icon the server makes.</summary>
/// <param name="Name">Its address part, for example <c>maskable-512</c>.</param>
/// <param name="Purpose">How it is drawn.</param>
/// <param name="Size">Width and height in pixels.</param>
public sealed record AppIconRendition(string Name, AppIconPurpose Purpose, int Size);

/// <summary>
/// The installable web app's icon (module 19, task J1): the tenant's square PNG app icon, at least
/// 512 pixels a side, made by the server into the sizes and maskable versions phones and
/// computers need.
/// </summary>
public static class AppIcons
{
    /// <summary>Smallest side of an uploaded app icon.</summary>
    public const int MinSide = 512;

    /// <summary>
    /// Share of a maskable icon the picture takes: the safe zone is a circle of 80 % of the icon,
    /// and a square fits inside it at 0.8 / √2 ≈ 0.566.
    /// </summary>
    public const double MaskableShare = 0.56;

    /// <summary>Share of an iOS icon the picture takes (a little margin, iOS rounds the corners).</summary>
    public const double AppleShare = 0.86;

    /// <summary>Every size the server makes.</summary>
    public static IReadOnlyList<AppIconRendition> Renditions { get; } =
    [
        new("any-192", AppIconPurpose.Any, 192),
        new("any-512", AppIconPurpose.Any, 512),
        new("maskable-192", AppIconPurpose.Maskable, 192),
        new("maskable-512", AppIconPurpose.Maskable, 512),
        new("apple-180", AppIconPurpose.Apple, 180),
    ];

    /// <summary>The rendition called <paramref name="name"/>, or null.</summary>
    public static AppIconRendition? Find(string? name) =>
        Renditions.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    /// <summary>Why an uploaded app icon cannot be used (code and message), or null when it can.</summary>
    public static (string Code, string Message)? Check(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (PngImage.ReadSize(png) is not { } size)
        {
            return ("png-unreadable", "The file is not a PNG image Zelvo can read.");
        }

        if (size.Width != size.Height)
        {
            return ("app-icon-not-square", "The app icon must be square (as wide as it is high).");
        }

        if (size.Width < MinSide)
        {
            return ("app-icon-too-small", $"The app icon must be at least {MinSide} × {MinSide} pixels.");
        }

        try
        {
            PngImage.Decode(png);
        }
        catch (PngFormatException exception)
        {
            return (exception.Code, exception.Message);
        }

        return null;
    }

    /// <summary>
    /// Makes <paramref name="rendition"/> of the icon. Maskable and iOS icons get the icon's own
    /// background when its corner is opaque, else <paramref name="fallbackBackground"/> (the
    /// manifest's background colour).
    /// </summary>
    public static byte[] Render(byte[] png, AppIconRendition rendition, (byte R, byte G, byte B) fallbackBackground)
    {
        ArgumentNullException.ThrowIfNull(rendition);
        var image = PngImage.Decode(png);
        var background = image.OpaqueCorner() ?? fallbackBackground;
        var result = rendition.Purpose switch
        {
            AppIconPurpose.Maskable => image.OnBackground(rendition.Size, background, MaskableShare),
            AppIconPurpose.Apple => image.OnBackground(rendition.Size, background, image.OpaqueCorner() is null ? AppleShare : 1.0),
            _ => image.Resize(rendition.Size, rendition.Size),
        };
        return result.Encode();
    }
}

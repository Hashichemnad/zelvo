using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zelvo.Data.Localization;

namespace Zelvo.Data.Branding;

/// <summary>Images a tenant's branding can have (module 11).</summary>
public enum BrandingSlot
{
    /// <summary>Logo on light backgrounds.</summary>
    LogoLight,

    /// <summary>Logo on dark backgrounds (dark mode).</summary>
    LogoDark,

    /// <summary>Browser tab icon.</summary>
    Favicon,

    /// <summary>Image of the sign-in page.</summary>
    LoginImage,
}

/// <summary>A font the branding may choose; every one is bundled with Zelvo (works offline) and has Arabic and Latin letters.</summary>
public static class BrandingFonts
{
    /// <summary>Zelvo's default.</summary>
    public const string Default = "ibm-plex-sans-arabic";

    /// <summary>Key → CSS family name.</summary>
    public static IReadOnlyDictionary<string, string> Families { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Default] = "IBM Plex Sans Arabic",
        ["cairo"] = "Cairo",
        ["tajawal"] = "Tajawal",
        ["almarai"] = "Almarai",
    };
}

/// <summary>
/// What a tenant's branding says (module 11): the product name in English and Arabic, brand
/// colours for light and dark mode, a font, and its images. Stored as JSON in
/// <see cref="TenantBranding"/>. Empty values mean Zelvo's default.
/// </summary>
public sealed partial record BrandingValue
{
    /// <summary>Zelvo's own brand colour in light mode.</summary>
    public const string DefaultLightColour = "#0f5c8a";

    /// <summary>Zelvo's own brand colour in dark mode.</summary>
    public const string DefaultDarkColour = "#7cc4f0";

    /// <summary>The light theme's surface (cards, emails, PDFs).</summary>
    public const string LightSurface = "#ffffff";

    /// <summary>The dark theme's surface.</summary>
    public const string DarkSurface = "#141a20";

    /// <summary>Maximum length of each language of the product name.</summary>
    public const int NameMaxLength = 80;

    /// <summary>WCAG 2.2 AA contrast for normal text.</summary>
    public const double MinimumContrast = 4.5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Product name shown instead of "Zelvo" (portal, workspace, browser tab, emails, chat widget).</summary>
    public LocalizedText ProductName { get; init; } = new(null, null);

    /// <summary>Brand colour in light mode (<c>#rrggbb</c>).</summary>
    public string LightColour { get; init; } = DefaultLightColour;

    /// <summary>Brand colour in dark mode.</summary>
    public string DarkColour { get; init; } = DefaultDarkColour;

    /// <summary>Font key (<see cref="BrandingFonts"/>).</summary>
    public string Font { get; init; } = BrandingFonts.Default;

    /// <summary>The uploaded image (an attachment id) per slot.</summary>
    public Dictionary<BrandingSlot, Guid> Images { get; init; } = [];

    /// <summary>Zelvo's default look.</summary>
    public static BrandingValue Default { get; } = new();

    /// <summary>Reads stored JSON; anything unreadable is the default.</summary>
    public static BrandingValue Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            return JsonSerializer.Deserialize<BrandingValue>(json, Json) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>As stored JSON.</summary>
    public string Write() => JsonSerializer.Serialize(this, Json);

    /// <summary>The product name in <paramref name="language"/>, else the other, else "Zelvo" in that language.</summary>
    public string ProductNameIn(string language) =>
        ProductName.GetOrFallback(language) ?? (language == LocalizedText.Arabic ? "زيلفو" : "Zelvo");

    /// <summary>The font's CSS family.</summary>
    public string FontFamily => BrandingFonts.Families.GetValueOrDefault(Font) ?? BrandingFonts.Families[BrandingFonts.Default];

    /// <summary>The automatic contrast checks (WCAG 2.2 AA); publishing needs all of them to pass.</summary>
    public IReadOnlyList<ContrastCheck> Checks() =>
    [
        new("light.text", LightColour, LightSurface, Contrast(LightColour, LightSurface)),
        new("light.button", LightSurface, LightColour, Contrast(LightSurface, LightColour)),
        new("dark.text", DarkColour, DarkSurface, Contrast(DarkColour, DarkSurface)),
        new("dark.button", DarkSurface, DarkColour, Contrast(DarkSurface, DarkColour)),
    ];

    /// <summary>Whether <paramref name="colour"/> is <c>#rrggbb</c>.</summary>
    public static bool IsColour(string? colour) => colour is not null && ColourPattern().IsMatch(colour);

    /// <summary>The WCAG contrast ratio of two <c>#rrggbb</c> colours (1–21), rounded to two decimals.</summary>
    public static double Contrast(string a, string b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return Math.Round((Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05), 2);
    }

    private static double Luminance(string colour)
    {
        if (!IsColour(colour))
        {
            return 0;
        }

        double Channel(int index)
        {
            var value = int.Parse(colour.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex ColourPattern();
}

/// <summary>One contrast check.</summary>
/// <param name="Key">What is checked: <c>light.text</c> (brand colour as text and links on the light surface), <c>light.button</c> (button text on the brand colour), and the same in dark mode.</param>
/// <param name="Foreground">Foreground colour.</param>
/// <param name="Background">Background colour.</param>
/// <param name="Ratio">Contrast ratio.</param>
public sealed record ContrastCheck(string Key, string Foreground, string Background, double Ratio)
{
    /// <summary>Meets WCAG 2.2 AA for normal text.</summary>
    public bool Passed => Ratio >= BrandingValue.MinimumContrast;
}

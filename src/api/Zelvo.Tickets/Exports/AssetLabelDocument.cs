using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zelvo.Data.Branding;
using Zelvo.Data.Localization;

namespace Zelvo.Tickets.Exports;

/// <summary>How big the labels of a sheet are.</summary>
public enum AssetLabelLayout
{
    /// <summary>A4 with 3 × 8 labels of 70 × 37 mm.</summary>
    Small,

    /// <summary>A4 with 2 × 5 labels of 99 × 57 mm.</summary>
    Large,
}

/// <summary>A label: the tag (encoded and printed) and the item's name (printed small).</summary>
public sealed record AssetLabel(string Tag, string Name);

/// <summary>What a label sheet export prints (its parameters).</summary>
public sealed record AssetLabelSheet(AssetLabelLayout Layout, IReadOnlyList<AssetLabel> Labels)
{
    /// <summary>How the parameters are stored.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The sheet in <paramref name="json"/>, or <see langword="null"/>.</summary>
    public static AssetLabelSheet? Read(string? json)
    {
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<AssetLabelSheet>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>As stored.</summary>
    public string Write() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// A printable A4 sheet of asset labels (task G3): each label has the asset tag as a QR code and
/// as Code 128 (product owner decision 16: the tag only), the tag in text, the item's name and the
/// organisation, in its brand colour. Drawn as inline SVG; the renderer runs no scripts.
/// </summary>
public static class AssetLabelDocument
{
    /// <summary>The HTML document.</summary>
    public static string Html(BrandingValue branding, LocalizedText organisation, AssetLabelSheet sheet, string language, string? fontsDirectory)
    {
        ArgumentNullException.ThrowIfNull(branding);
        ArgumentNullException.ThrowIfNull(sheet);
        var rtl = language == LocalizedText.Arabic;
        var small = sheet.Layout == AssetLabelLayout.Small;
        var (columns, rows, width, height, qr) = small ? (3, 8, 70.0, 37.0, 25.0) : (2, 5, 99.0, 57.0, 38.0);
        var colour = BrandingValue.IsColour(branding.LightColour) ? branding.LightColour : BrandingValue.DefaultLightColour;
        var font = branding.FontFamily;
        var owner = organisation.GetOrFallback(language) ?? branding.ProductNameIn(language);
        var html = new StringBuilder(16384);
        html.Append(CultureInfo.InvariantCulture, $$"""
            <!DOCTYPE html>
            <html lang="{{language}}" dir="{{(rtl ? "rtl" : "ltr")}}">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src file: data:">
            <title>{{E(ExportTexts.Get(language, "assetLabels"))}}</title>
            <style>
            {{TicketDocument.FontFaces(font, fontsDirectory)}}
            @page { size: A4; margin: 10mm 0; }
            body { font-family: "{{font}}", "Noto Sans Arabic", "Noto Sans", Arial, sans-serif; color: #1a1f24; margin: 0; }
            .sheet { display: grid; grid-template-columns: repeat({{columns}}, {{width}}mm); grid-auto-rows: {{height}}mm; justify-content: center; }
            .sheet + .sheet { break-before: page; }
            .label { box-sizing: border-box; padding: 3mm; display: flex; gap: 2.5mm; align-items: center; overflow: hidden; border: 0.2mm dashed #c9d1d9; }
            .label .codes { display: flex; flex-direction: column; gap: 1.5mm; min-width: 0; }
            .tag { font-family: monospace; font-weight: 700; font-size: {{(small ? 10 : 14)}}pt; letter-spacing: 0.3pt; }
            .name { font-size: {{(small ? 7 : 9)}}pt; color: #4a5560; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .owner { font-size: {{(small ? 6.5 : 8)}}pt; font-weight: 700; color: {{colour}}; }
            </style>
            </head>
            <body>
            """);
        var perSheet = columns * rows;
        for (var start = 0; start < sheet.Labels.Count; start += perSheet)
        {
            html.Append("<div class=\"sheet\">\n");
            foreach (var label in sheet.Labels.Skip(start).Take(perSheet))
            {
                var barWidth = width - qr - 3 * 2 - 2.5;
                html.Append(CultureInfo.InvariantCulture, $"""
                    <div class="label">{Barcodes.QrSvg(label.Tag, qr)}<div class="codes">
                    <div class="owner" dir="auto">{E(owner)}</div>
                    <div class="tag" dir="ltr">{E(label.Tag)}</div>
                    {Barcodes.Code128Svg(label.Tag, barWidth, small ? 8 : 12)}
                    <div class="name" dir="auto">{E(label.Name)}</div>
                    </div></div>
                    """).Append('\n');
            }

            html.Append("</div>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}

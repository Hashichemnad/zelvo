using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zelvo.Integrations;

/// <summary>
/// Masks personal data and secrets in payloads before they are stored in the integration log
/// (module 14: "payload (personal data masked)"). Two layers:
/// <list type="bullet">
/// <item>JSON properties whose name says they hold personal data or secrets (email, phone,
/// Emirates ID, names, passwords, tokens…) are replaced whole, whatever their value.</item>
/// <item>Every remaining text is scanned for values that look like personal data: email
/// addresses, Emirates ID numbers, UAE and international phone numbers, IBANs and card numbers.</item>
/// </list>
/// Masking errs on the side of hiding: it is for logs that people read, never for data Zelvo uses.
/// </summary>
public static partial class PersonalDataMasker
{
    /// <summary>What masked values are replaced with.</summary>
    public const string Mask = "[masked]";

    // Compared after removing '_', '-' and case, so "first_name", "firstName" and "First-Name" match.
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.Ordinal)
    {
        "email", "emailaddress", "mail", "phone", "phonenumber", "mobile", "mobilenumber", "telephone", "fax",
        "emiratesid", "nationalid", "idnumber", "passport", "passportnumber", "uaepassuuid",
        "name", "displayname", "firstname", "lastname", "middlename", "fullname", "givenname", "familyname", "surname",
        "address", "street", "postaddress", "dateofbirth", "birthdate", "dob",
        "iban", "accountnumber", "cardnumber", "pan", "cvv",
        "password", "secret", "clientsecret", "token", "accesstoken", "refreshtoken", "idtoken",
        "apikey", "authorization", "cookie", "signature", "otp",
    };

    // Names that end like a sensitive one but are not personal data (for example "roleName" stays).
    private static readonly string[] SensitiveSuffixes = ["email", "phone", "mobile", "password", "secret", "token", "apikey", "emiratesid"];

    /// <summary>True when a JSON property or query parameter with this name holds personal data or a secret.</summary>
    public static bool IsSensitiveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalised = new string([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        return SensitiveNames.Contains(normalised) || SensitiveSuffixes.Any(s => normalised.EndsWith(s, StringComparison.Ordinal));
    }

    /// <summary>
    /// Masks a body for the log: JSON is masked by property and value; other text by value only.
    /// The result is cut to <paramref name="maxLength"/> characters.
    /// </summary>
    public static string? MaskBody(string? body, string? contentType, int maxLength)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        string masked;
        if (LooksLikeJson(body, contentType) && TryParse(body) is { } node)
        {
            masked = MaskNode(node)?.ToJsonString(CompactJson) ?? "null";
        }
        else if (contentType?.Contains("form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
        {
            masked = MaskQueryString(body);
        }
        else
        {
            masked = MaskText(body);
        }

        return masked.Length <= maxLength ? masked : string.Concat(masked.AsSpan(0, maxLength - 1), "…");
    }

    /// <summary>Masks every value of a JSON document (see the class summary).</summary>
    public static JsonNode? MaskNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    obj[key] = IsSensitiveName(key) && value is not null and not JsonObject and not JsonArray
                        ? Mask
                        : IsSensitiveName(key) && value is JsonArray array
                            ? new JsonArray([.. array.Select(_ => (JsonNode?)Mask)])
                            : MaskNode(value?.DeepClone());
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = MaskNode(array[i]?.DeepClone());
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                var masked = MaskText(text);
                return masked == text ? value : JsonValue.Create(masked);
            default:
                return node;
        }
    }

    /// <summary>Masks values that look like personal data in free text.</summary>
    public static string MaskText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var masked = EmailPattern().Replace(text, Mask);
        masked = EmiratesIdPattern().Replace(masked, Mask);
        masked = IbanPattern().Replace(masked, Mask);
        masked = BearerPattern().Replace(masked, "Bearer " + Mask);
        masked = ApiKeyPattern().Replace(masked, Mask);
        var ids = UuidPattern().Matches(masked).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        masked = LongNumberPattern().Replace(masked, m =>
            !ids.Any(id => m.Index < id.End && m.Index + m.Length > id.Index) && IsPersonalNumber(m.Value) ? Mask : m.Value);
        return masked;
    }

    /// <summary>
    /// Masks a query string (<c>a=1&amp;email=x</c>, with or without the leading <c>?</c>): values of
    /// sensitive parameters whole, the others by value.
    /// </summary>
    public static string MaskQueryString(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var prefix = query.StartsWith('?') ? "?" : string.Empty;
        var parts = query.TrimStart('?').Split('&');
        var builder = new StringBuilder(prefix);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            var separator = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                builder.Append(parts[i]);
                continue;
            }

            var name = parts[i][..separator];
            var value = Uri.UnescapeDataString(parts[i][(separator + 1)..].Replace('+', ' '));
            builder.Append(name).Append('=');
            builder.Append(IsSensitiveName(Uri.UnescapeDataString(name)) ? Mask : Uri.EscapeDataString(MaskText(value)));
        }

        return builder.ToString();
    }

    private static bool LooksLikeJson(string body, string? contentType) =>
        contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
        || body.TrimStart() is ['{', ..] or ['[', ..];

    private static JsonNode? TryParse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Phone, card and account numbers: 9 or more digits, allowing spaces and dashes inside.
    // Plain numbers that are clearly not personal (short counts, years, epoch times) are kept.
    internal static bool IsPersonalNumber(string value)
    {
        var digits = value.Count(char.IsAsciiDigit);
        if (value.StartsWith('+'))
        {
            return digits >= 8;
        }

        // Unix timestamps in seconds (10 digits starting with 1) and milliseconds (13) stay readable.
        var plain = value.All(char.IsAsciiDigit);
        if (plain && ((digits == 10 && value[0] == '1') || (digits == 13 && value[0] == '1')))
        {
            return false;
        }

        return digits >= 9;
    }

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    internal static partial Regex EmailPattern();

    [GeneratedRegex(@"\b784[\s\-]?\d{4}[\s\-]?\d{7}[\s\-]?\d\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    internal static partial Regex EmiratesIdPattern();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){3,7}(?:\s?[A-Z0-9]{1,4})?\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    internal static partial Regex IbanPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    internal static partial Regex BearerPattern();

    [GeneratedRegex(@"\b(?:zvk|whsec)_[A-Za-z0-9_\-+/=]{8,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    internal static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    internal static partial Regex UuidPattern();

    [GeneratedRegex(@"\+?\d[\d\s\-]{7,}\d", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    internal static partial Regex LongNumberPattern();
}

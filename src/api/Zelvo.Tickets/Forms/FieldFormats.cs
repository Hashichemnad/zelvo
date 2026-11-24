using System.Globalization;
using System.Net.Mail;
using System.Text;

namespace Zelvo.Tickets.Forms;

/// <summary>
/// Format checks for form answers (module 2: Emirates ID, phone, email, IBAN). Each returns the
/// value in a single normal form, or <see langword="null"/> when it is not valid. The web app has
/// the same checks (<c>src/web/src/lib/forms/formats.ts</c>); shared test cases keep them equal.
/// </summary>
public static class FieldFormats
{
    /// <summary>IBAN lengths of countries Zelvo's customers deal with most; others are checked by the general rules only.</summary>
    private static readonly Dictionary<string, int> IbanLengths = new(StringComparer.Ordinal)
    {
        ["AE"] = 23, ["SA"] = 24, ["QA"] = 29, ["KW"] = 30, ["BH"] = 22, ["OM"] = 23, ["JO"] = 30, ["EG"] = 29,
        ["LB"] = 28, ["IQ"] = 23, ["PK"] = 24, ["TR"] = 26, ["GB"] = 22, ["DE"] = 22, ["FR"] = 27, ["NL"] = 18,
        ["CH"] = 21, ["IE"] = 22, ["ES"] = 24, ["IT"] = 27,
    };

    /// <summary>Checks <paramref name="value"/> against <paramref name="format"/>.</summary>
    public static string? Normalize(FieldFormat format, string value) => format switch
    {
        FieldFormat.EmiratesId => EmiratesId(value),
        FieldFormat.Phone => Phone(value),
        FieldFormat.Email => Email(value),
        FieldFormat.Iban => Iban(value),
        _ => null,
    };

    /// <summary>
    /// An Emirates ID number: 15 digits starting with 784 (dashes and spaces allowed), whose last
    /// digit is the Luhn check digit of the others. Arabic-Indic digits are accepted.
    /// </summary>
    public static string? EmiratesId(string value)
    {
        var digits = Digits(value, "- ");
        if (digits is null || digits.Length != 15 || !digits.StartsWith("784", StringComparison.Ordinal) || !Luhn(digits))
        {
            return null;
        }

        return digits;
    }

    /// <summary>
    /// A phone number in E.164 form (<c>+971501234567</c>). Spaces, dashes, dots and brackets are
    /// allowed; <c>00</c> may stand for <c>+</c>; UAE numbers may be written locally (<c>050 123 4567</c>, <c>04 123 4567</c>).
    /// </summary>
    public static string? Phone(string value)
    {
        var trimmed = value.Trim();
        var plus = trimmed.StartsWith('+');
        var digits = Digits(plus ? trimmed[1..] : trimmed, " -.()");
        if (digits is null || digits.Length == 0)
        {
            return null;
        }

        string international;
        if (plus)
        {
            international = digits;
        }
        else if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            international = digits[2..];
        }
        else if (digits.StartsWith('0') && digits.Length is 9 or 10)
        {
            // UAE: 0 + area code or mobile prefix.
            international = "971" + digits[1..];
        }
        else
        {
            return null;
        }

        if (international.Length is < 7 or > 15 || international[0] == '0')
        {
            return null;
        }

        // UAE: mobile numbers (5x) have 9 digits after 971, landlines 8.
        if (international.StartsWith("971", StringComparison.Ordinal) && international.Length != (international.Length > 3 && international[3] == '5' ? 12 : 11))
        {
            return null;
        }

        return "+" + international;
    }

    /// <summary>An email address, lower-cased in the domain, at most 254 characters.</summary>
    public static string? Email(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > 254 || !MailAddress.TryCreate(trimmed, out var address)
            || address.Address != trimmed || address.DisplayName.Length > 0 || !address.Host.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var at = trimmed.LastIndexOf('@');
        return trimmed[..at] + "@" + trimmed[(at + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// An IBAN: country code, two check digits and up to 30 letters and digits (spaces allowed),
    /// whose ISO 13616 check (mod 97) is 1. Known countries must have their own length (AE: 23).
    /// </summary>
    public static string? Iban(string value)
    {
        var compact = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c))
            {
                return null;
            }

            compact.Append(char.ToUpperInvariant(c));
        }

        var iban = compact.ToString();
        if (iban.Length is < 15 or > 34 || !char.IsAsciiLetter(iban[0]) || !char.IsAsciiLetter(iban[1])
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]))
        {
            return null;
        }

        if (IbanLengths.TryGetValue(iban[..2], out var length) && iban.Length != length)
        {
            return null;
        }

        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            var number = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = number >= 10 ? ((remainder * 100) + number) % 97 : ((remainder * 10) + number) % 97;
        }

        return remainder == 1 ? iban : null;
    }

    /// <summary>The Luhn check over all digits (the last one is the check digit).</summary>
    internal static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    /// <summary>ASCII digits of <paramref name="value"/> (Arabic-Indic and Persian digits converted), skipping <paramref name="separators"/>; null when anything else is there.</summary>
    private static string? Digits(string value, string separators)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (separators.Contains(c, StringComparison.Ordinal))
            {
                continue;
            }

            var digit = CharUnicodeInfo.GetDecimalDigitValue(c);
            if (digit < 0 || (c > '9' && c is not (>= '٠' and <= '٩') and not (>= '۰' and <= '۹')))
            {
                return null;
            }

            builder.Append((char)('0' + digit));
        }

        return builder.ToString();
    }
}

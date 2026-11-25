using System.Security.Cryptography;

namespace Zelvo.Licensing;

/// <summary>
/// The hidden customer watermark (module 12): a short code we give each customer, written into its
/// license, its builds, its database and the files Zelvo generates (PDF and Excel exports, emails),
/// so a leaked copy or file can be traced to the customer it was issued to. The code means nothing
/// on its own: our customer sheet maps it to the customer. Format <c>ZW-XXXX-XXXX-XXXX-C</c> in
/// Crockford base32 with a check character.
/// </summary>
public static class Watermark
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A new random watermark.</summary>
    public static string Create()
    {
        Span<char> body = stackalloc char[12];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var code = new string(body);
        return $"ZW-{code[..4]}-{code[4..8]}-{code[8..]}-{Check(code)}";
    }

    /// <summary>Whether the value is a well-formed watermark with a correct check character.</summary>
    public static bool IsValid(string? value)
    {
        if (value is not { Length: 19 } || !value.StartsWith("ZW-", StringComparison.Ordinal) || value[7] != '-' || value[12] != '-' || value[17] != '-')
        {
            return false;
        }

        var code = string.Concat(value.AsSpan(3, 4), value.AsSpan(8, 4), value.AsSpan(13, 4));
        return code.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) && Check(code) == value[18];
    }

    private static char Check(string code)
    {
        var sum = 0;
        for (var i = 0; i < code.Length; i++)
        {
            sum += (i + 1) * Alphabet.IndexOf(code[i], StringComparison.Ordinal);
        }

        return Alphabet[sum % Alphabet.Length];
    }
}

/// <summary>The watermark of this installation, for the places generated files carry it.</summary>
public interface IInstallationWatermark
{
    /// <summary>The license's watermark (or the build's), or null in development.</summary>
    string? Current { get; }
}

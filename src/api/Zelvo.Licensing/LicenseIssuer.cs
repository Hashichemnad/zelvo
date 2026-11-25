using System.Security.Cryptography;
using System.Text.Json;

namespace Zelvo.Licensing;

/// <summary>What a new license says, besides the servers from the activation request.</summary>
/// <param name="LicenseId">License id (for example <c>LIC-2026-0001</c>).</param>
/// <param name="Kind">Production, Test or Demo.</param>
/// <param name="CustomerId">Our customer id.</param>
/// <param name="CustomerNameEn">The customer's name in English.</param>
/// <param name="CustomerNameAr">The customer's name in Arabic.</param>
/// <param name="Watermark">The customer's watermark (<see cref="Licensing.Watermark.Create"/>).</param>
/// <param name="Modules">Licensed modules.</param>
/// <param name="Version">Feature version bought (major.minor).</param>
internal sealed record LicenseTerms(
    string LicenseId, LicenseKind Kind, string CustomerId, string? CustomerNameEn, string? CustomerNameAr, string Watermark,
    IReadOnlyList<string> Modules, Version Version);

/// <summary>
/// Turns an activation request into a signed license or upgrade license (module 12). Used by the
/// offline signing tool on the product owner's computer, and by tests with throwaway keys. The
/// license is bound to exactly the fingerprint in the request.
/// </summary>
internal static class LicenseIssuer
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static SignedFile Issue(ActivationRequestFile request, LicenseTerms terms, string keyId, RSA privateKey, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Kind is not (LicenseKind.Production or LicenseKind.Test or LicenseKind.Demo))
        {
            throw new ArgumentException("A license is for production, test or demo.", nameof(terms));
        }

        if (!Watermark.IsValid(terms.Watermark))
        {
            throw new ArgumentException("The watermark is not valid.", nameof(terms));
        }

        var unknown = terms.Modules.Where(m => !LicensedModules.Phase1.Contains(m)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown modules: {string.Join(", ", unknown)}.", nameof(terms));
        }

        var payload = new LicensePayload
        {
            Type = SignedFileTypes.License,
            LicenseId = terms.LicenseId,
            Kind = terms.Kind.ToString(),
            Customer = new LicenseCustomer(terms.CustomerId, new BilingualName(terms.CustomerNameEn, terms.CustomerNameAr)),
            Watermark = terms.Watermark,
            Modules = [.. terms.Modules.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Version = terms.Version.ToString(2),
            IssuedAt = issuedAt,
            ActivationRequestId = request.RequestId,
            Fingerprint = request.Fingerprint,
        };
        return SignedFile.Create(SignedFileTypes.License, JsonSerializer.SerializeToUtf8Bytes(payload, Json), keyId, privateKey);
    }

    /// <summary>An upgrade license unlocking <paramref name="version"/> for the license installed on the requesting servers.</summary>
    public static SignedFile Upgrade(ActivationRequestFile request, string licenseId, string upgradeId, Version version, string keyId, RSA privateKey, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = new LicensePayload
        {
            Type = SignedFileTypes.Upgrade,
            LicenseId = licenseId,
            UpgradeId = upgradeId,
            Version = version.ToString(2),
            IssuedAt = issuedAt,
            ActivationRequestId = request.RequestId,
            Fingerprint = request.Fingerprint,
        };
        return SignedFile.Create(SignedFileTypes.Upgrade, JsonSerializer.SerializeToUtf8Bytes(payload, Json), keyId, privateKey);
    }

    /// <summary>The public key entry for <c>vendor-keys.json</c>.</summary>
    public static VendorKey PublicKeyOf(string keyId, RSA key) => new(keyId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

    /// <summary>Reads a signed file's payload without checking it (the tool's inspect command).</summary>
    public static LicensePayload? Peek(SignedFile file) => file.PayloadBytes() is { } bytes ? LicensePayload.Parse(bytes) : null;
}

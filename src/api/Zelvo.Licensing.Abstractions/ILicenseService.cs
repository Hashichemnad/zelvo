namespace Zelvo.Licensing;

/// <summary>
/// The licensing contract every Zelvo service depends on (module 12: licensing and copy protection,
/// module 13: offline update packages).
/// </summary>
/// <remarks>
/// The implementation (<c>Zelvo.Licensing</c>) verifies the signed license file, the installation's
/// fingerprint and watermark, and our signatures on update packages. Feature code asks this
/// interface and never checks licenses itself.
/// </remarks>
public interface ILicenseService
{
    /// <summary>
    /// Verifies the installed license. Every service calls this at startup; a tampered license
    /// stops the service, and an installation without a license for these servers runs only its
    /// activation screens.
    /// </summary>
    Task<LicenseStatus> VerifyAsync(CancellationToken cancellationToken = default);

    /// <summary>The status from the last verification.</summary>
    LicenseStatus Current { get; }

    /// <summary>True when the given module (see <see cref="LicensedModules"/>) is licensed.</summary>
    bool IsModuleLicensed(string moduleId);

    /// <summary>
    /// True when the installation may run the given product version. Security and bug-fix releases
    /// are covered by the existing license; feature releases need an upgrade license.
    /// </summary>
    bool IsVersionLicensed(Version version);

    /// <summary>Creates the activation request file the customer sends to us during setup.</summary>
    Task<ActivationRequest> CreateActivationRequestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a license or upgrade-license file issued by us. The file is checked (signature,
    /// these servers, installation profile) before it is kept; a refused file changes nothing.
    /// </summary>
    Task<LicenseImportResult> ImportLicenseAsync(Stream licenseFile, LicenseImportContext context, CancellationToken cancellationToken = default);

    /// <summary>The installation's fingerprint as it is now, and whether each part matches the license.</summary>
    Task<FingerprintReport> GetFingerprintAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="signature"/> is our signature of <paramref name="data"/> with the
    /// vendor key <paramref name="keyId"/> (update packages, module 13).
    /// </summary>
    bool VerifyVendorSignature(ReadOnlySpan<byte> data, string keyId, ReadOnlySpan<byte> signature);
}

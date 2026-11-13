namespace Zelvo.Licensing;

/// <summary>
/// The licensing contract every Zelvo service depends on (module 12: licensing and copy protection,
/// module 13: offline update packages).
/// </summary>
/// <remarks>
/// This repository holds the contract and a development stub only. The production implementation
/// (signature verification, installation fingerprinting, watermarking) lives in the private
/// <c>zelvo-licensing</c> repository and is swapped in at release build time.
/// </remarks>
public interface ILicenseService
{
    /// <summary>
    /// Verifies the installed license. Every service calls this once at startup and refuses to start
    /// when the result is not valid.
    /// </summary>
    Task<LicenseStatus> VerifyAsync(CancellationToken cancellationToken = default);

    /// <summary>The status from the last successful verification.</summary>
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

    /// <summary>Imports a license or upgrade-license file issued by us and re-verifies.</summary>
    Task<LicenseStatus> ImportLicenseAsync(Stream licenseFile, CancellationToken cancellationToken = default);
}

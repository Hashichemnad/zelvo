using System.Text;
using Microsoft.Extensions.Hosting;

namespace Zelvo.Licensing;

/// <summary>
/// Development stub for <see cref="ILicenseService"/>: every module and version is allowed.
/// </summary>
/// <remarks>
/// It contains no license checks, fingerprinting, watermarking or keys, by design. It refuses to
/// run when the host environment is Production, so a development build cannot be used as a
/// production installation. Release builds replace this project with the private implementation.
/// </remarks>
internal sealed class DevelopmentLicenseService(IHostEnvironment environment) : ILicenseService
{
    internal const string LicensedToName = "Development build - not for production use";

    private static readonly LicenseStatus Allowed = new(
        IsValid: true,
        Kind: LicenseKind.Development,
        LicensedTo: LicensedToName,
        LicenseId: "dev",
        Modules: LicensedModules.Phase1.ToHashSet(StringComparer.Ordinal));

    private LicenseStatus _current = LicenseStatus.Invalid("Not verified yet.");

    public LicenseStatus Current => _current;

    public Task<LicenseStatus> VerifyAsync(CancellationToken cancellationToken = default)
    {
        _current = environment.IsProduction()
            ? LicenseStatus.Invalid(
                "This is a development build with the development licensing stub; it cannot run in Production.")
            : Allowed;
        return Task.FromResult(_current);
    }

    public bool IsModuleLicensed(string moduleId) => _current.IsValid;

    public bool IsVersionLicensed(Version version) => _current.IsValid;

    public Task<ActivationRequest> CreateActivationRequestAsync(CancellationToken cancellationToken = default)
    {
        var content = Encoding.UTF8.GetBytes("development-build: activation is not available\n");
        return Task.FromResult(new ActivationRequest("zelvo-activation-request.dev.txt", content));
    }

    public Task<LicenseStatus> ImportLicenseAsync(Stream licenseFile, CancellationToken cancellationToken = default) =>
        VerifyAsync(cancellationToken);
}

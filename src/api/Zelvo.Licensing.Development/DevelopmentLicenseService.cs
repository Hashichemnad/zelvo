using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Zelvo.Licensing;

/// <summary>
/// Development stub for <see cref="ILicenseService"/>: every module and version is allowed.
/// </summary>
/// <remarks>
/// It contains no license checks, fingerprinting, watermarking or keys, by design. It refuses to
/// run when the host environment is Production, so a development build cannot be used as a
/// production installation. Release builds replace this project with the private implementation.
/// With <c>Installation:Profile</c> set to <c>Demo</c> it reports a demo license
/// (<see cref="LicenseKind.Demo"/>), standing in for the demo license file a release demo
/// installation is given; with <c>Test</c>, a test license (<see cref="LicenseKind.Test"/>), as
/// for the test installation every license includes (module 14).
/// </remarks>
internal sealed class DevelopmentLicenseService(IHostEnvironment environment, IConfiguration? configuration = null) : ILicenseService
{
    internal const string LicensedToName = "Development build - not for production use";
    internal const string DemoLicensedToName = "Demo installation - development build";
    internal const string TestLicensedToName = "Test installation - development build";

    private LicenseStatus _current = LicenseStatus.Invalid("Not verified yet.");

    public LicenseStatus Current => _current;

    public Task<LicenseStatus> VerifyAsync(CancellationToken cancellationToken = default)
    {
        if (environment.IsProduction())
        {
            _current = LicenseStatus.Invalid(
                "This is a development build with the development licensing stub; it cannot run in Production.");
            return Task.FromResult(_current);
        }

        var profile = configuration?[InstallationProfile.ConfigurationKey];
        if (!string.IsNullOrEmpty(profile)
            && !string.Equals(profile, InstallationProfile.Standard, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile, InstallationProfile.Demo, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile, InstallationProfile.Test, StringComparison.OrdinalIgnoreCase))
        {
            _current = LicenseStatus.Invalid($"Unknown {InstallationProfile.ConfigurationKey} '{profile}': use Standard, Demo or Test.");
            return Task.FromResult(_current);
        }

        var demo = string.Equals(profile, InstallationProfile.Demo, StringComparison.OrdinalIgnoreCase);
        var test = string.Equals(profile, InstallationProfile.Test, StringComparison.OrdinalIgnoreCase);
        _current = new LicenseStatus(
            IsValid: true,
            Kind: demo ? LicenseKind.Demo : test ? LicenseKind.Test : LicenseKind.Development,
            LicensedTo: demo ? DemoLicensedToName : test ? TestLicensedToName : LicensedToName,
            LicenseId: demo ? "dev-demo" : test ? "dev-test" : "dev",
            Modules: LicensedModules.Phase1.ToHashSet(StringComparer.Ordinal));
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

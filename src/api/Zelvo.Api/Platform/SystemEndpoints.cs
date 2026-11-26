using System.Reflection;
using Zelvo.Licensing;

namespace Zelvo.Api.Platform;

/// <summary>Public, non-sensitive information about this installation.</summary>
internal static class SystemEndpoints
{
    internal static readonly string[] SupportedLanguages = ["en", "ar"];

    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/system/info", GetInfo)
            .WithName("GetSystemInfo")
            .WithTags("System")
            .WithSummary("Product name, version, supported languages, license mode, environment (production, test, demo or development), whether the installation is activated and the licensed modules (task F3), so screens can hide modules the license does not include.");
        return group;
    }

    internal static SystemInfo GetInfo(ILicenseService licenseService)
    {
        var version = typeof(SystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        // Strip the source-revision suffix ("+<sha>") so commit hashes are not disclosed.
        var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            version = version[..plusIndex];
        }

        var current = licenseService.Current;
        return new SystemInfo("Zelvo", version, SupportedLanguages, current.Kind.ToString(), licenseService.EnvironmentName(),
            current.IsValid, current.ActivationRequired, [.. LicensedModules.Phase1.Where(licenseService.IsModuleLicensed)]);
    }
}

/// <summary>Response body of <c>GET /api/v1/system/info</c>.</summary>
/// <param name="Product">Platform name.</param>
/// <param name="Version">Product version.</param>
/// <param name="Languages">Supported UI languages.</param>
/// <param name="LicenseMode">Kind of license the installation runs under.</param>
/// <param name="Environment">What the installation is for: <c>production</c>, <c>test</c> (the test installation every license includes), <c>demo</c> or <c>development</c>.</param>
/// <param name="Activated">A valid license applies (task F3).</param>
/// <param name="ActivationRequired">The installation waits for a license for these servers; only the activation screens work.</param>
/// <param name="Modules">Licensed modules (<c>m01-incidents</c> and so on).</param>
internal sealed record SystemInfo(
    string Product, string Version, IReadOnlyList<string> Languages, string LicenseMode, string Environment, bool Activated, bool ActivationRequired,
    IReadOnlyList<string> Modules);

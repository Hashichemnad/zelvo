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
            .WithSummary("Product name, version, supported languages, license mode and environment (production, test, demo or development).");
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

        return new SystemInfo("Zelvo", version, SupportedLanguages, licenseService.Current.Kind.ToString(), licenseService.EnvironmentName());
    }
}

/// <summary>Response body of <c>GET /api/v1/system/info</c>.</summary>
/// <param name="Product">Platform name.</param>
/// <param name="Version">Product version.</param>
/// <param name="Languages">Supported UI languages.</param>
/// <param name="LicenseMode">Kind of license the installation runs under.</param>
/// <param name="Environment">What the installation is for: <c>production</c>, <c>test</c> (the test installation every license includes), <c>demo</c> or <c>development</c>.</param>
internal sealed record SystemInfo(string Product, string Version, IReadOnlyList<string> Languages, string LicenseMode, string Environment);

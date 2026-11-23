namespace Zelvo.Licensing;

/// <summary>
/// What an installation is for. A demo installation (demo mode in <c>docs/requirements.md</c>) has
/// its own database, sample organisations and a demo license; it never holds customer data.
/// </summary>
public static class InstallationProfile
{
    /// <summary>Configuration key (<c>Installation__Profile</c> in the environment).</summary>
    public const string ConfigurationKey = "Installation:Profile";

    /// <summary>An installation for real use (the default).</summary>
    public const string Standard = "Standard";

    /// <summary>A demo installation with sample data.</summary>
    public const string Demo = "Demo";

    /// <summary>
    /// The separate test installation every license includes (module 14), where integrations are
    /// built and tried away from live data. Runs under a test license.
    /// </summary>
    public const string Test = "Test";
}

/// <summary>Helpers on <see cref="ILicenseService"/>.</summary>
public static class LicenseServiceExtensions
{
    /// <summary>
    /// True when the installation runs under a valid demo license. Demo features (sample data,
    /// one-click demo users, "Reset demo") are available only then.
    /// </summary>
    public static bool IsDemo(this ILicenseService licenseService)
    {
        ArgumentNullException.ThrowIfNull(licenseService);
        var current = licenseService.Current;
        return current.IsValid && current.Kind == LicenseKind.Demo;
    }

    /// <summary>True when the installation runs under a valid test license (the test installation of module 14).</summary>
    public static bool IsTestInstallation(this ILicenseService licenseService)
    {
        ArgumentNullException.ThrowIfNull(licenseService);
        var current = licenseService.Current;
        return current.IsValid && current.Kind == LicenseKind.Test;
    }

    /// <summary>
    /// What the installation is, as integrations see it: <c>production</c>, <c>test</c>, <c>demo</c>
    /// or <c>development</c>. Sent in webhooks and in the <c>Zelvo-Environment</c> API header so
    /// that connected systems can tell test data from live data.
    /// </summary>
    public static string EnvironmentName(this ILicenseService licenseService)
    {
        ArgumentNullException.ThrowIfNull(licenseService);
        return licenseService.Current.Kind switch
        {
            LicenseKind.Production => "production",
            LicenseKind.Test => "test",
            LicenseKind.Demo => "demo",
            _ => "development",
        };
    }
}

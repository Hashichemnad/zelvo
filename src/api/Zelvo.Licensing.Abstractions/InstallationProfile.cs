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
}

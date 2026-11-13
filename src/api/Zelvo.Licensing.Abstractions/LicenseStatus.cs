namespace Zelvo.Licensing;

/// <summary>What kind of license the installation runs under.</summary>
public enum LicenseKind
{
    /// <summary>No license installed or verification failed.</summary>
    None = 0,

    /// <summary>Development stub: everything allowed, development builds only.</summary>
    Development = 1,

    /// <summary>Demo installation license (demo mode).</summary>
    Demo = 2,

    /// <summary>A customer's permanent production license.</summary>
    Production = 3,

    /// <summary>The test installation every license includes (module 14).</summary>
    Test = 4,
}

/// <summary>Result of a license verification.</summary>
/// <param name="IsValid">Whether the installation may run.</param>
/// <param name="Kind">The kind of license.</param>
/// <param name="LicensedTo">Customer name the license was issued to.</param>
/// <param name="LicenseId">License identifier, when one is installed.</param>
/// <param name="Modules">Licensed module identifiers.</param>
/// <param name="Reason">Why verification failed, when it did. Safe to log; contains no secrets.</param>
public sealed record LicenseStatus(
    bool IsValid,
    LicenseKind Kind,
    string LicensedTo,
    string? LicenseId,
    IReadOnlySet<string> Modules,
    string? Reason = null)
{
    /// <summary>A status for an installation with no valid license.</summary>
    public static LicenseStatus Invalid(string reason) =>
        new(false, LicenseKind.None, string.Empty, null, new HashSet<string>(), reason);
}

/// <summary>An activation request file produced during setup.</summary>
/// <param name="FileName">Suggested file name.</param>
/// <param name="Content">File content, sent to us by the customer.</param>
public sealed record ActivationRequest(string FileName, ReadOnlyMemory<byte> Content);

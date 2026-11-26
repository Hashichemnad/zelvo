namespace Zelvo.Licensing;

/// <summary>What kind of license the installation runs under.</summary>
public enum LicenseKind
{
    /// <summary>No license installed or verification failed.</summary>
    None = 0,

    /// <summary>Development license: everything allowed, development builds only.</summary>
    Development = 1,

    /// <summary>Demo installation license (demo mode).</summary>
    Demo = 2,

    /// <summary>A customer's permanent production license.</summary>
    Production = 3,

    /// <summary>The test installation every license includes (module 14).</summary>
    Test = 4,
}

/// <summary>Why a license is not valid, for screens and logs (no secrets).</summary>
public static class LicenseProblems
{
    /// <summary>No license is installed: the installation waits for activation.</summary>
    public const string NotActivated = "not-activated";

    /// <summary>The license was issued for other servers (or the servers changed): a new activation is needed.</summary>
    public const string FingerprintMismatch = "fingerprint-mismatch";

    /// <summary>The license is for another kind of installation (for example a demo license on a standard installation).</summary>
    public const string KindMismatch = "kind-mismatch";

    /// <summary>The license was issued to another customer than this build.</summary>
    public const string WatermarkMismatch = "watermark-mismatch";

    /// <summary>The license file was changed after we signed it, or is not ours.</summary>
    public const string SignatureInvalid = "signature-invalid";

    /// <summary>The license names a signing key this build does not trust.</summary>
    public const string UnknownKey = "unknown-key";

    /// <summary>The file is not a Zelvo license.</summary>
    public const string FormatInvalid = "format-invalid";

    /// <summary>An upgrade license for another license than the installed one.</summary>
    public const string UpgradeMismatch = "upgrade-mismatch";

    /// <summary>The licenses could not be read (database not reachable).</summary>
    public const string StoreUnavailable = "store-unavailable";

    /// <summary>A development build refuses to run in the Production environment.</summary>
    public const string DevelopmentInProduction = "development-in-production";

    /// <summary>
    /// True when the problem means the installation was changed by someone (the service refuses to
    /// start), as opposed to waiting for an activation.
    /// </summary>
    public static bool IsTampering(string? code) => code is SignatureInvalid or UnknownKey or FormatInvalid or WatermarkMismatch;
}

/// <summary>Result of a license verification.</summary>
/// <param name="IsValid">Whether the installation may run.</param>
/// <param name="Kind">The kind of license.</param>
/// <param name="LicensedTo">Customer name the license was issued to (English).</param>
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
    /// <summary>Why the license is not valid (<see cref="LicenseProblems"/>); null when it is.</summary>
    public string? ProblemCode { get; init; }

    /// <summary>The customer name in Arabic, when the license has one.</summary>
    public string? LicensedToAr { get; init; }

    /// <summary>Our customer id the license was issued to.</summary>
    public string? CustomerId { get; init; }

    /// <summary>
    /// Feature version (major.minor) the license covers, raised by upgrade licenses; null when
    /// every version is allowed (development license).
    /// </summary>
    public Version? LicensedVersion { get; init; }

    /// <summary>Feature versions unlocked by upgrade licenses.</summary>
    public IReadOnlyList<Version> Upgrades { get; init; } = [];

    /// <summary>When we issued the license.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>The hidden customer watermark the license carries.</summary>
    public string? Watermark { get; init; }

    /// <summary>
    /// True when the installation waits for an activation (no license, or one for other servers):
    /// services run only their activation screens.
    /// </summary>
    public bool ActivationRequired => !IsValid && ProblemCode is LicenseProblems.NotActivated or LicenseProblems.FingerprintMismatch
        or LicenseProblems.KindMismatch or LicenseProblems.StoreUnavailable;

    /// <summary>A status for an installation with no valid license.</summary>
    public static LicenseStatus Invalid(string reason) =>
        new(false, LicenseKind.None, string.Empty, null, new HashSet<string>(), reason);

    /// <summary>A status for an installation with no valid license, with its problem code.</summary>
    public static LicenseStatus Invalid(string problemCode, string reason) =>
        new(false, LicenseKind.None, string.Empty, null, new HashSet<string>(), reason) { ProblemCode = problemCode };
}

/// <summary>An activation request file produced during setup.</summary>
/// <param name="FileName">Suggested file name.</param>
/// <param name="Content">File content, sent to us by the customer.</param>
public sealed record ActivationRequest(string FileName, ReadOnlyMemory<byte> Content);

/// <summary>Who imports a license file (kept with it).</summary>
/// <param name="UserId">The administrator's user id, when a person imports it.</param>
/// <param name="TenantId">The tenant the administrator acted in.</param>
/// <param name="FileName">The uploaded file's name.</param>
public sealed record LicenseImportContext(Guid? UserId, Guid? TenantId, string? FileName);

/// <summary>Result of importing a license file.</summary>
/// <param name="Accepted">Whether the file was kept and now applies.</param>
/// <param name="ProblemCode">Why it was refused (<see cref="LicenseProblems"/>).</param>
/// <param name="Reason">Why it was refused, in English for the screen and the audit log.</param>
/// <param name="FileType">What the file was: <c>License</c> or <c>Upgrade</c>, when it could be read.</param>
/// <param name="LicenseId">The license (or upgrade) id in the file, when it could be read.</param>
/// <param name="Status">The installation's status after the import.</param>
public sealed record LicenseImportResult(bool Accepted, string? ProblemCode, string? Reason, string? FileType, string? LicenseId, LicenseStatus Status);

/// <summary>One part of the installation fingerprint.</summary>
/// <param name="Key">Part: <c>installationId</c>, <c>databaseId</c>, <c>machineId</c>, <c>clusterId</c> or <c>domain</c>.</param>
/// <param name="Value">The value now (machine ids shortened for the screen).</param>
/// <param name="Matches">Whether the installed license accepts it; null without a license or when the license does not bind it.</param>
public sealed record FingerprintPart(string Key, string? Value, bool? Matches);

/// <summary>The installation fingerprint as it is now.</summary>
/// <param name="Parts">Its parts.</param>
public sealed record FingerprintReport(IReadOnlyList<FingerprintPart> Parts);

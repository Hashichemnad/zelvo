namespace Zelvo.Data.Installation;

/// <summary>Where an imported update package is (module 13, task F4).</summary>
public enum UpdatePackageStatus
{
    /// <summary>Checked and kept; can be installed.</summary>
    Imported,

    /// <summary>Installed.</summary>
    Installed,

    /// <summary>Removed by an administrator; its file is deleted.</summary>
    Removed,
}

/// <summary>Where an install is.</summary>
public enum UpdateInstallState
{
    /// <summary>Waiting for its time (the maintenance window) or for the updater.</summary>
    Scheduled,

    /// <summary>The updater is installing it.</summary>
    Running,

    /// <summary>Installed and healthy.</summary>
    Succeeded,

    /// <summary>A step failed and the previous version was restored.</summary>
    RolledBack,

    /// <summary>A step failed and restoring the previous version failed too; needs the operator.</summary>
    Failed,

    /// <summary>Cancelled before it started.</summary>
    Cancelled,
}

/// <summary>
/// An update package imported in Settings → Updates (module 13). Installation-wide, not tenant
/// data: no tenant column or row-level security. The package file is kept on the updates volume.
/// </summary>
public sealed class UpdatePackage
{
    public const int VersionMaxLength = 30;
    public const int FileNameMaxLength = 200;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Version the package installs (major.minor.patch).</summary>
    public required string Version { get; init; }

    /// <summary><c>Security</c> or <c>Feature</c>.</summary>
    public required string PackageType { get; init; }

    /// <summary>The uploaded file's name.</summary>
    public required string FileName { get; init; }

    /// <summary>Where the file is kept (under the updates directory).</summary>
    public required string StoragePath { get; set; }

    public long SizeBytes { get; init; }

    /// <summary>SHA-256 of the whole file; the updater checks it again.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The signed manifest's contents (JSON).</summary>
    public required string Manifest { get; init; }

    public UpdatePackageStatus Status { get; set; } = UpdatePackageStatus.Imported;

    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;

    public Guid? ImportedByUserId { get; init; }

    public Guid? ImportedByTenantId { get; init; }
}

/// <summary>A step of an install with how it went.</summary>
/// <param name="Key">verify, load-images, stop, backup, switch-version, migrate, start, health, rollback-database, rollback-configuration, rollback-start.</param>
/// <param name="State">Pending, Running, Done, Failed or Skipped.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndedAt">When it ended.</param>
/// <param name="Message">What happened, in English for the operator (no secrets).</param>
public sealed record UpdateInstallStep(string Key, string State, DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null, string? Message = null);

/// <summary>An install of an update package, run by the updater service (module 13).</summary>
public sealed class UpdateInstall
{
    public const int MessageMaxLength = 2000;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid PackageId { get; init; }

    public required string FromVersion { get; init; }

    public required string ToVersion { get; init; }

    public UpdateInstallState State { get; set; } = UpdateInstallState.Scheduled;

    /// <summary>When it may start (now, or in the maintenance window).</summary>
    public DateTimeOffset ScheduledFor { get; set; }

    /// <summary>From when users see the notice banner.</summary>
    public DateTimeOffset NoticeFrom { get; set; }

    /// <summary>How long users are told it takes.</summary>
    public int ExpectedMinutes { get; set; }

    public Guid? RequestedByUserId { get; init; }

    /// <summary>The tenant whose audit log records the install (the operator tenant).</summary>
    public Guid? RequestedByTenantId { get; init; }

    /// <summary>The steps with their outcome (JSON list of <see cref="UpdateInstallStep"/>).</summary>
    public string Steps { get; set; } = "[]";

    /// <summary>Why it failed or was rolled back.</summary>
    public string? Error { get; set; }

    /// <summary>Where the backup of the database and configuration is.</summary>
    public string? BackupPath { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

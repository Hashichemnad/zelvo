using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Npgsql;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Installation;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

namespace Zelvo.Api.Installation;

/// <summary>The running version, replaceable in tests.</summary>
internal sealed class InstalledVersionProvider
{
    public Version Current { get; init; } = InstalledVersion.Current;
}

/// <summary>
/// Settings → Updates (module 13, task F4): import signed update packages (upload, or a file carried
/// in on USB and put in the updates inbox), see every check, install now or in a maintenance window
/// with a notice to users beforehand, follow the install step by step, and the history. The install
/// itself is done by the updater service, which checks the package again, backs up the database and
/// configuration and rolls back by itself when a step fails. Needs <c>installation.manage</c>; every
/// import, install, cancellation and outcome is in the audit log.
/// </summary>
internal static partial class UpdateEndpoints
{
    /// <summary>The updater counts as online when it said so within this time.</summary>
    private static readonly TimeSpan UpdaterOnline = TimeSpan.FromMinutes(2);

    public static RouteGroupBuilder MapUpdateEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/system/maintenance", MaintenanceAsync).WithName("GetMaintenance").WithTags("System").AllowAnonymous()
            .WithSummary("The update users are told about: version, when it starts and how long it takes, from the time the administrator chose to show the notice until it ends; whether it is being installed now. Open to everyone.")
            .Produces<MaintenanceResponse>();

        var updates = group.MapGroup("/updates").WithTags("Updates").RequireTenant();
        updates.MapGet("/", GetAsync).WithName("GetUpdates")
            .WithSummary("The installed version, whether the updater service is online, imported packages with whether each can be installed now, files waiting in the updates inbox, and the install history. Needs installation.manage.")
            .RequirePermission(Permissions.InstallationManage).Produces<UpdatesResponse>();
        updates.MapPost("/packages", UploadAsync).WithName("UploadUpdatePackage")
            .WithSummary("Upload a signed update package (.zpkg, multipart field 'file', streamed to disk). Checked before it is kept: Zelvo's signature, every file against the manifest, newer than the installed version, the type fits the version change, a feature release needs its upgrade license, and the images this installation runs. A refused package is deleted and answers 422 with the reason (package-signature-invalid, package-not-newer, package-upgrade-license-required and so on). Needs installation.manage; audited either way.")
            .RequirePermission(Permissions.InstallationManage).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(long.MaxValue), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = long.MaxValue })
            .Accepts<IFormFile>("multipart/form-data").Produces<UpdatePackageResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        updates.MapPost("/inbox/{fileName}/import", ImportFromInboxAsync).WithName("ImportUpdatePackageFromInbox")
            .WithSummary("Import a package the operator copied into the updates inbox (for air-gapped networks: carried in on USB), with the same checks as an upload. Needs installation.manage; audited.")
            .RequirePermission(Permissions.InstallationManage).Produces<UpdatePackageResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        updates.MapDelete("/packages/{id:guid}", RemoveAsync).WithName("RemoveUpdatePackage")
            .WithSummary("Remove an imported package that is not being installed; its file is deleted. Needs installation.manage; audited.")
            .RequirePermission(Permissions.InstallationManage).Produces(StatusCodes.Status204NoContent);
        updates.MapPost("/packages/{id:guid}/install", InstallAsync).WithName("InstallUpdatePackage")
            .WithSummary("Install a package now or in a maintenance window (scheduledFor), with a notice banner for users from noticeFrom (default: 24 hours before, or now). The checks run again first. One install at a time. Needs installation.manage; audited.")
            .RequirePermission(Permissions.InstallationManage).Produces<UpdateInstallResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        updates.MapGet("/installs/{id:guid}", GetInstallAsync).WithName("GetUpdateInstall")
            .WithSummary("An install with its steps. Needs installation.manage.")
            .RequirePermission(Permissions.InstallationManage).Produces<UpdateInstallResponse>();
        updates.MapDelete("/installs/{id:guid}", CancelAsync).WithName("CancelUpdateInstall")
            .WithSummary("Cancel an install that has not started. Needs installation.manage; audited.")
            .RequirePermission(Permissions.InstallationManage).Produces<UpdateInstallResponse>();
        return group;
    }

    internal static async Task<IResult> MaintenanceAsync(ZelvoDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var install = await db.UpdateInstalls.AsNoTracking()
            .Where(i => (i.State == UpdateInstallState.Scheduled && i.NoticeFrom <= now) || i.State == UpdateInstallState.Running)
            .OrderBy(i => i.ScheduledFor).FirstOrDefaultAsync(cancellationToken);
        return TypedResults.Ok(install is null
            ? new MaintenanceResponse(false, false, null, null, null)
            : new MaintenanceResponse(true, install.State == UpdateInstallState.Running, install.ToVersion, install.ScheduledFor, install.ExpectedMinutes));
    }

    internal static async Task<IResult> GetAsync(
        ZelvoDbContext db, ILicenseService license, IOptions<UpdateOptions> options, InstalledVersionProvider installed, TimeProvider time, CancellationToken cancellationToken)
    {
        var packages = await db.UpdatePackages.AsNoTracking().Where(p => p.Status != UpdatePackageStatus.Removed)
            .OrderByDescending(p => p.ImportedAt).Take(50).ToListAsync(cancellationToken);
        var installs = await db.UpdateInstalls.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(50).ToListAsync(cancellationToken);
        var seen = await UpdaterSeenAtAsync(db, cancellationToken);
        var inbox = Directory.Exists(options.Value.InboxDirectory)
            ? Directory.EnumerateFiles(options.Value.InboxDirectory, "*.zpkg").Select(f => new FileInfo(f))
                .Select(f => new InboxFileResponse(f.Name, f.Length, f.LastWriteTimeUtc)).OrderBy(f => f.FileName, StringComparer.Ordinal).ToList()
            : [];
        return TypedResults.Ok(new UpdatesResponse(
            installed.Current.ToString(3), seen is { } s && time.GetUtcNow() - s < UpdaterOnline, seen,
            [.. packages.Select(p => UpdatePackageResponse.From(p, license, installed.Current, options.Value))],
            [.. installs.Select(UpdateInstallResponse.From)], inbox, options.Value.MaxPackageBytes));
    }

    internal static async Task<IResult> UploadAsync(
        HttpRequest http, ZelvoDbContext db, ILicenseService license, IOptions<UpdateOptions> options, InstalledVersionProvider installed, CurrentUser user,
        ITenantContext tenant, IAuditLog audit, CancellationToken cancellationToken)
    {
        var boundary = MediaTypeHeaderValue.TryParse(http.ContentType, out var type) && type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            ? HeaderUtilities.RemoveQuotes(type.Boundary).Value : null;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200)
        {
            return UserEndpoints.Validation("file", "Send the package as multipart form data in the field 'file'.");
        }

        Directory.CreateDirectory(options.Value.PackagesDirectory);
        var path = Path.Combine(options.Value.PackagesDirectory, $"{Guid.CreateVersion7():N}.zpkg");
        string? fileName = null;
        try
        {
            var reader = new MultipartReader(boundary, http.Body);
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.IsFileDisposition()
                    || disposition.Name.Value != "file")
                {
                    continue;
                }

                fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(disposition.FileName.HasValue ? disposition.FileName : disposition.FileNameStar).Value) ?? "package.zpkg";
                await using var target = File.Create(path);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > options.Value.MaxPackageBytes)
                    {
                        target.Close();
                        File.Delete(path);
                        return ApiProblem.Create(StatusCodes.Status413PayloadTooLarge, "package-too-large",
                            "The package is larger than this installation accepts by upload; copy it into the updates inbox instead.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or BadHttpRequestException)
        {
            File.Delete(path);
            return UserEndpoints.Validation("file", "The upload did not complete. Please try again.");
        }

        if (fileName is null || !File.Exists(path))
        {
            return UserEndpoints.Validation("file", "Choose the update package (.zpkg) Zelvo sent you.");
        }

        return await KeepAsync(path, fileName, db, license, options.Value, installed.Current, user, tenant, audit, cancellationToken);
    }

    internal static async Task<IResult> ImportFromInboxAsync(
        string fileName, ZelvoDbContext db, ILicenseService license, IOptions<UpdateOptions> options, InstalledVersionProvider installed, CurrentUser user,
        ITenantContext tenant, IAuditLog audit, CancellationToken cancellationToken)
    {
        // Only a plain file name in the inbox: no paths.
        var name = Path.GetFileName(fileName);
        var source = Path.Combine(options.Value.InboxDirectory, name);
        if (name != fileName || !name.EndsWith(".zpkg", StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "inbox-file-not-found", "There is no such package in the updates inbox.");
        }

        Directory.CreateDirectory(options.Value.PackagesDirectory);
        var path = Path.Combine(options.Value.PackagesDirectory, $"{Guid.CreateVersion7():N}.zpkg");
        File.Move(source, path);
        return await KeepAsync(path, name, db, license, options.Value, installed.Current, user, tenant, audit, cancellationToken);
    }

    // Checks a package on disk; keeps it and its record, or deletes it.
    private static async Task<IResult> KeepAsync(
        string path, string fileName, ZelvoDbContext db, ILicenseService license, UpdateOptions options, Version installed, CurrentUser user,
        ITenantContext tenant, IAuditLog audit, CancellationToken cancellationToken)
    {
        var check = await UpdatePackageReader.CheckAsync(path, license, installed, options.Images, cancellationToken);
        if (!check.Passed)
        {
            File.Delete(path);
            await audit.RecordAsync(AuditCategory.Admin, "update.package.refused", "update-package", null, new Dictionary<string, object?>
            {
                ["fileName"] = fileName, ["problem"] = check.Problem, ["version"] = check.Manifest?.Version, ["sha256"] = check.Sha256,
            }, cancellationToken);
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, check.Problem!, check.Reason!);
        }

        var manifest = check.Manifest!;
        var package = new UpdatePackage
        {
            Version = manifest.ParsedVersion!.ToString(3), PackageType = manifest.Type.ToString(), FileName = Truncate(fileName, UpdatePackage.FileNameMaxLength),
            StoragePath = path, SizeBytes = new FileInfo(path).Length, Sha256 = check.Sha256,
            Manifest = JsonSerializer.Serialize(manifest with { Files = manifest.Files }, ManifestJson),
            ImportedByUserId = user.Account?.Id, ImportedByTenantId = tenant.TenantId,
        };
        db.UpdatePackages.Add(package);
        audit.Add(AuditCategory.Admin, "update.package.imported", "update-package", package.Id.ToString(), new Dictionary<string, object?>
        {
            ["fileName"] = fileName, ["version"] = package.Version, ["type"] = package.PackageType, ["sha256"] = package.Sha256, ["checks"] = check.Checks,
        });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/updates/packages/{package.Id}", UpdatePackageResponse.From(package, license, installed, options));
    }

    internal static async Task<IResult> RemoveAsync(Guid id, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var package = await db.UpdatePackages.SingleOrDefaultAsync(p => p.Id == id && p.Status != UpdatePackageStatus.Removed, cancellationToken);
        if (package is null)
        {
            return NotFound();
        }

        if (await db.UpdateInstalls.AnyAsync(i => i.PackageId == id && (i.State == UpdateInstallState.Scheduled || i.State == UpdateInstallState.Running), cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "package-in-use", "Cancel the package's install first.");
        }

        package.Status = UpdatePackageStatus.Removed;
        if (File.Exists(package.StoragePath))
        {
            File.Delete(package.StoragePath);
        }

        audit.Add(AuditCategory.Admin, "update.package.removed", "update-package", id.ToString(), new Dictionary<string, object?> { ["version"] = package.Version });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> InstallAsync(
        Guid id, InstallRequest? request, ZelvoDbContext db, ILicenseService license, IOptions<UpdateOptions> options, InstalledVersionProvider installed,
        CurrentUser user, ITenantContext tenant, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        var package = await db.UpdatePackages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.Status == UpdatePackageStatus.Imported, cancellationToken);
        if (package is null)
        {
            return NotFound();
        }

        var manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(package.Manifest, ManifestJson)!;
        var check = UpdatePackageReader.CheckInstallable(manifest, package.Sha256, license, installed.Current, options.Value.Images);
        if (!check.Passed)
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, check.Problem!, check.Reason!);
        }

        var now = time.GetUtcNow();
        var scheduledFor = request?.ScheduledFor ?? now;
        if (scheduledFor < now.AddMinutes(-1) || scheduledFor > now.AddDays(90))
        {
            return UserEndpoints.Validation("scheduledFor", "Choose a time from now up to 90 days ahead.");
        }

        var noticeFrom = request?.NoticeFrom ?? (scheduledFor.AddHours(-24) < now ? now : scheduledFor.AddHours(-24));
        if (noticeFrom > scheduledFor)
        {
            return UserEndpoints.Validation("noticeFrom", "Show the notice before the install starts.");
        }

        var install = new UpdateInstall
        {
            PackageId = id, FromVersion = installed.Current.ToString(3), ToVersion = package.Version, ScheduledFor = scheduledFor, NoticeFrom = noticeFrom,
            ExpectedMinutes = Math.Clamp(manifest.ExpectedMinutes, 1, 1440), RequestedByUserId = user.Account?.Id, RequestedByTenantId = tenant.TenantId,
        };
        db.UpdateInstalls.Add(install);
        audit.Add(AuditCategory.Admin, "update.install.scheduled", "update-install", install.Id.ToString(), new Dictionary<string, object?>
        {
            ["fromVersion"] = install.FromVersion, ["toVersion"] = install.ToVersion, ["scheduledFor"] = scheduledFor, ["noticeFrom"] = noticeFrom,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "install-pending", "Another install is scheduled or running; cancel it or wait until it ends.");
        }

        return TypedResults.Created($"/api/v1/updates/installs/{install.Id}", UpdateInstallResponse.From(install));
    }

    internal static async Task<IResult> GetInstallAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.UpdateInstalls.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken) is { } install
            ? TypedResults.Ok(UpdateInstallResponse.From(install))
            : NotFound();

    internal static async Task<IResult> CancelAsync(Guid id, ZelvoDbContext db, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        // Only while the updater has not claimed it; the update is a single statement, so the two never both win.
        var now = time.GetUtcNow();
        var changed = await db.UpdateInstalls.Where(i => i.Id == id && i.State == UpdateInstallState.Scheduled)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.State, UpdateInstallState.Cancelled).SetProperty(i => i.FinishedAt, now).SetProperty(i => i.UpdatedAt, now), cancellationToken);
        var install = await db.UpdateInstalls.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (install is null)
        {
            return NotFound();
        }

        if (changed == 0)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "install-not-scheduled", "Only an install that has not started can be cancelled.");
        }

        await audit.RecordAsync(AuditCategory.Admin, "update.install.cancelled", "update-install", id.ToString(),
            new Dictionary<string, object?> { ["toVersion"] = install.ToVersion }, cancellationToken);
        return TypedResults.Ok(UpdateInstallResponse.From(install));
    }

    internal static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static async Task<DateTimeOffset?> UpdaterSeenAtAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<DateTimeOffset?>($"SELECT updater_seen_at AS \"Value\" FROM installation").SingleOrDefaultAsync(cancellationToken);

    private static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "not-found", "There is no such update package or install.");

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>When to install.</summary>
/// <param name="ScheduledFor">Start time (the maintenance window); now when left out.</param>
/// <param name="NoticeFrom">From when users see the notice; 24 hours before (or now) when left out.</param>
internal sealed record InstallRequest(DateTimeOffset? ScheduledFor, DateTimeOffset? NoticeFrom);

/// <summary>The notice users see.</summary>
internal sealed record MaintenanceResponse(bool Announced, bool InProgress, string? Version, DateTimeOffset? ScheduledFor, int? ExpectedMinutes);

/// <summary>A file in the updates inbox.</summary>
internal sealed record InboxFileResponse(string FileName, long SizeBytes, DateTime ModifiedAt);

/// <summary>Settings → Updates.</summary>
internal sealed record UpdatesResponse(
    string InstalledVersion, bool UpdaterOnline, DateTimeOffset? UpdaterSeenAt, IReadOnlyList<UpdatePackageResponse> Packages,
    IReadOnlyList<UpdateInstallResponse> Installs, IReadOnlyList<InboxFileResponse> Inbox, long MaxPackageBytes);

/// <summary>An imported package and whether it can be installed now.</summary>
internal sealed record UpdatePackageResponse(
    Guid Id, string Version, string Type, string FileName, long SizeBytes, string Sha256, UpdatePackageStatus Status, DateTimeOffset ImportedAt,
    UpdateReleaseNotes ReleaseNotes, int ExpectedMinutes, bool Installable, string? Problem, string? Reason)
{
    public static UpdatePackageResponse From(UpdatePackage package, ILicenseService license, Version installed, UpdateOptions options)
    {
        var manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(package.Manifest, UpdateEndpoints.ManifestJson)!;
        var check = package.Status == UpdatePackageStatus.Imported
            ? UpdatePackageReader.CheckInstallable(manifest, package.Sha256, license, installed, options.Images)
            : null;
        return new UpdatePackageResponse(package.Id, package.Version, package.PackageType, package.FileName, package.SizeBytes, package.Sha256, package.Status,
            package.ImportedAt, manifest.ReleaseNotes, manifest.ExpectedMinutes, check?.Passed ?? false, check?.Problem, check?.Reason);
    }
}

/// <summary>An install with its steps.</summary>
internal sealed record UpdateInstallResponse(
    Guid Id, Guid PackageId, string FromVersion, string ToVersion, UpdateInstallState State, DateTimeOffset ScheduledFor, DateTimeOffset NoticeFrom,
    int ExpectedMinutes, IReadOnlyList<UpdateInstallStep> Steps, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt)
{
    public static UpdateInstallResponse From(UpdateInstall install) => new(
        install.Id, install.PackageId, install.FromVersion, install.ToVersion, install.State, install.ScheduledFor, install.NoticeFrom, install.ExpectedMinutes,
        JsonSerializer.Deserialize<List<UpdateInstallStep>>(install.Steps, UpdateEndpoints.ManifestJson) ?? [], install.Error, install.CreatedAt, install.StartedAt,
        install.FinishedAt);
}

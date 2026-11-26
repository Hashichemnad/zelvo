using Microsoft.AspNetCore.Mvc;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

namespace Zelvo.Api.Installation;

/// <summary>
/// Settings → License (module 12, task F3): the installation's license, its activation request and
/// the import of license and upgrade-license files. Everything here works while the installation
/// waits for activation. Needs <c>installation.manage</c> (tenant administrators of the operator
/// tenant; never for integrations).
/// </summary>
internal static class LicenseEndpoints
{
    private const long MaxLicenseFileBytes = 1024 * 1024;

    public static RouteGroupBuilder MapLicenseEndpoints(this RouteGroupBuilder group)
    {
        var license = group.MapGroup("/license").WithTags("License").RequireTenant();
        license.MapGet("/", GetAsync).WithName("GetLicense")
            .WithSummary("The installation's license: whether it is activated (or why not), the customer, the license id, licensed modules and version, upgrade licenses, the installed version, the fingerprint of these servers part by part and the license files kept. Never shows the watermark. Needs installation.manage.")
            .RequirePermission(Permissions.InstallationManage).Produces<LicenseResponse>();
        license.MapGet("/components", ComponentsAsync).WithName("GetReleaseComponents")
            .WithSummary("The release integrity of the installation's components (module 12 tamper detection): the signed release manifest's version and, per component (api, worker, updater, web, images), its last check: Verified, NoManifest (development builds), ManifestInvalid, ComponentUnknown, Modified, or from the updater's image check ImagesVerified or ImageMismatch. A component that fails refuses to start in release builds. Needs installation.manage.")
            .RequirePermission(Permissions.InstallationManage).Produces<ReleaseComponentsResponse>();
        license.MapPost("/activation-request", ActivationRequestAsync).WithName("CreateActivationRequest")
            .WithSummary("Download a new activation request file for these servers (installation id, database id, machine or cluster id, domain, version) to send to Zelvo, which returns a license file bound to them. Holds no secrets. Needs installation.manage; audited.")
            .RequirePermission(Permissions.InstallationManage).Produces(StatusCodes.Status200OK, contentType: "application/json");
        license.MapPost("/import", ImportAsync).WithName("ImportLicense")
            .WithSummary("Import a license or upgrade-license file (multipart field 'file', up to 1 MB). It is checked first (Zelvo's signature, these servers, the installation profile, for an upgrade the installed license) and kept only when every check passes; a new license replaces the old one. A refused file answers 422 with the reason (license-signature-invalid, license-fingerprint-mismatch and so on). Needs installation.manage; audited either way.")
            .RequirePermission(Permissions.InstallationManage).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxLicenseFileBytes + 64 * 1024))
            .Accepts<IFormFile>("multipart/form-data").Produces<LicenseResponse>().ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return group;
    }

    internal static async Task<IResult> GetAsync(ILicenseService license, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ResponseAsync(license, cancellationToken));

    internal static async Task<IResult> ComponentsAsync(
        IReleaseComponentStore store, ReleaseIntegrityOptions options, ImageSigningKeys keys, CancellationToken cancellationToken)
    {
        var manifest = ReleaseIntegrity.ReadManifest(options.ManifestPath, keys, out var status, out var reason);
        var reports = await store.ListAsync(cancellationToken);
        return TypedResults.Ok(new ReleaseComponentsResponse(
            manifest?.Version, status.ToString(), reason, VendorKeys.IsReleaseBuild,
            [.. reports.Select(r => new ReleaseComponentResponse(r.Component, r.Status, r.Version, r.BuildVersion, r.Detail, r.CheckedAt))]));
    }

    internal static async Task<IResult> ActivationRequestAsync(ILicenseService license, IAuditLog audit, CancellationToken cancellationToken)
    {
        var request = await license.CreateActivationRequestAsync(cancellationToken);
        await audit.RecordAsync(AuditCategory.Admin, "license.activation-requested", "installation", null,
            new Dictionary<string, object?> { ["fileName"] = request.FileName }, cancellationToken);
        return TypedResults.File(request.Content.ToArray(), "application/json", request.FileName);
    }

    internal static async Task<IResult> ImportAsync(
        HttpRequest http, ILicenseService license, CurrentUser user, ITenantContext tenant, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!http.HasFormContentType)
        {
            return UserEndpoints.Validation("file", "Send the license file as multipart form data in the field 'file'.");
        }

        var form = await http.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return UserEndpoints.Validation("file", "Choose the license file Zelvo sent you.");
        }

        if (file.Length > MaxLicenseFileBytes)
        {
            return Refused("license-format-invalid", "The file is too large to be a Zelvo license.");
        }

        var fileName = Path.GetFileName(file.FileName);
        await using var stream = file.OpenReadStream();
        var result = await license.ImportLicenseAsync(stream, new LicenseImportContext(user.Account?.Id, tenant.TenantId, fileName), cancellationToken);
        await audit.RecordAsync(AuditCategory.Admin, result.Accepted ? "license.imported" : "license.import-refused", "installation", result.LicenseId,
            new Dictionary<string, object?>
            {
                ["fileName"] = fileName, ["fileType"] = result.FileType, ["problem"] = result.ProblemCode,
                ["kind"] = result.Accepted ? result.Status.Kind.ToString() : null,
                ["modules"] = result.Accepted ? result.Status.Modules.Order(StringComparer.Ordinal).ToArray() : null,
            }, cancellationToken);
        return result.Accepted
            ? TypedResults.Ok(await ResponseAsync(license, cancellationToken))
            : Refused($"license-{result.ProblemCode ?? LicenseProblems.FormatInvalid}", result.Reason ?? "The file was refused.");
    }

    private static IResult Refused(string code, string reason) => ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, code, reason);

    internal static async Task<LicenseResponse> ResponseAsync(ILicenseService license, CancellationToken cancellationToken)
    {
        var status = license.Current;
        var fingerprint = await license.GetFingerprintAsync(cancellationToken);
        IReadOnlyList<LicenseFileResponse> files = [];
        if (license is LicenseService service)
        {
            files = [.. (await service.ListFilesAsync(cancellationToken)).Select(f => new LicenseFileResponse(f.FileType, f.LicenseId, f.ImportedAt, f.FileName))];
        }

        return new LicenseResponse(
            status.IsValid, status.ActivationRequired, status.Kind, status.ProblemCode, status.IsValid ? null : status.Reason,
            status.LicenseId, status.IsValid ? new LicenseNameResponse(status.LicensedTo, status.LicensedToAr) : null, status.CustomerId,
            status.LicensedVersion?.ToString(2), [.. status.Upgrades.Select(u => u.ToString(2))], status.IssuedAt,
            LicenseService.ProductVersion(), VendorKeys.IsReleaseBuild,
            [.. LicensedModules.Phase1.Select(m => new LicensedModuleResponse(m, license.IsModuleLicensed(m)))],
            [.. fingerprint.Parts.Select(p => new FingerprintPartResponse(p.Key, p.Value, p.Matches))],
            files);
    }
}

/// <summary>The installation's release and each component's last integrity check.</summary>
/// <param name="Release">The signed release manifest's version, or null without a valid manifest.</param>
/// <param name="ManifestStatus">Verified, NoManifest or ManifestInvalid.</param>
/// <param name="ManifestReason">The same in words.</param>
/// <param name="ReleaseBuild">Whether failed checks stop components (release builds).</param>
/// <param name="Components">Each component's last report.</param>
internal sealed record ReleaseComponentsResponse(
    string? Release, string ManifestStatus, string ManifestReason, bool ReleaseBuild, IReadOnlyList<ReleaseComponentResponse> Components);

/// <summary>A component's last release integrity check.</summary>
internal sealed record ReleaseComponentResponse(string Component, string Status, string? Release, string BuildVersion, string Detail, DateTimeOffset CheckedAt);

/// <summary>A name in English and Arabic.</summary>
internal sealed record LicenseNameResponse(string? En, string? Ar);

/// <summary>A module and whether the license includes it.</summary>
internal sealed record LicensedModuleResponse(string Key, bool Licensed);

/// <summary>A part of the fingerprint.</summary>
internal sealed record FingerprintPartResponse(string Key, string? Value, bool? Matches);

/// <summary>A license file kept by the installation.</summary>
internal sealed record LicenseFileResponse(string FileType, string LicenseId, DateTimeOffset ImportedAt, string? FileName);

/// <summary>The installation's license.</summary>
/// <param name="Activated">A valid license (or the development license) applies.</param>
/// <param name="ActivationRequired">The installation waits for a license for these servers.</param>
/// <param name="Kind">License kind.</param>
/// <param name="Problem">Why it is not valid (not-activated, fingerprint-mismatch and so on).</param>
/// <param name="Reason">The same in words.</param>
/// <param name="LicenseId">License id.</param>
/// <param name="LicensedTo">Customer name.</param>
/// <param name="CustomerId">Our customer id.</param>
/// <param name="LicensedVersion">Feature version the license covers (major.minor); null for the development license.</param>
/// <param name="Upgrades">Feature versions unlocked by upgrade licenses.</param>
/// <param name="IssuedAt">When it was issued.</param>
/// <param name="InstalledVersion">The running version.</param>
/// <param name="ReleaseBuild">Whether this is a release licensing build.</param>
/// <param name="Modules">Every module with whether it is licensed.</param>
/// <param name="Fingerprint">These servers' fingerprint.</param>
/// <param name="Files">License and upgrade-license files kept.</param>
internal sealed record LicenseResponse(
    bool Activated, bool ActivationRequired, LicenseKind Kind, string? Problem, string? Reason, string? LicenseId, LicenseNameResponse? LicensedTo,
    string? CustomerId, string? LicensedVersion, IReadOnlyList<string> Upgrades, DateTimeOffset? IssuedAt, string InstalledVersion, bool ReleaseBuild,
    IReadOnlyList<LicensedModuleResponse> Modules, IReadOnlyList<FingerprintPartResponse> Fingerprint, IReadOnlyList<LicenseFileResponse> Files);

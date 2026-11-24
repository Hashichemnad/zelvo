using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Branding;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;
using Zelvo.Secrets;
using Zelvo.Tickets;
using Zelvo.Tickets.Branding;

namespace Zelvo.Api.Branding;

/// <summary>Installation settings of branding (configuration section <c>Branding</c>), set by the operator.</summary>
internal sealed class BrandingOptions
{
    /// <summary>The installation's public host that custom domains point to with a CNAME, for example <c>zelvo.example.ae</c>.</summary>
    public string? PublicHost { get; set; }

    /// <summary>Shared secret of the reverse proxy's TLS questions (<c>/internal/tls/...</c>); without it they answer 404.</summary>
    public string? TlsToken { get; set; }

    /// <summary>What the SPF record of a sender domain must contain for the installation's mail server, for example <c>include:spf.mail.example.ae</c>.</summary>
    public string? SpfInclude { get; set; }
}

/// <summary>
/// White-label branding (module 11, task F1): the published look for everyone (product name,
/// colours, font, logos, favicon, sign-in image), Settings → Branding with a draft, the automatic
/// contrast checks and publishing, custom domains with automatic or uploaded TLS certificates, the
/// tenant's own email sender domain with a guided SPF, DKIM and DMARC check, and branded PDF
/// exports. The "Powered by Zelvo" mark is not part of branding: it is always shown.
/// </summary>
internal static partial class BrandingEndpoints
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The sign-in pages of {Tenant} were not updated; publishing again retries.")]
    private static partial void SignInNotUpdated(ILogger logger, Exception exception, string tenant);

    /// <summary>Largest image in bytes (the favicon: <see cref="MaxFaviconBytes"/>).</summary>
    public const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>Largest favicon in bytes.</summary>
    public const int MaxFaviconBytes = 256 * 1024;

    private static readonly string[] ImageTypes = ["png", "jpg", "jpeg", "webp"];

    public static IServiceCollection AddZelvoBranding(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BrandingOptions>(configuration.GetSection("Branding"));
        return services;
    }

    public static IEndpointRouteBuilder MapBrandingEndpoints(this IEndpointRouteBuilder app, RouteGroupBuilder group)
    {
        var branding = group.MapGroup("/branding").WithTags("Branding").RequireTenant();
        branding.MapGet("/", PublishedAsync)
            .WithName("GetBranding")
            .WithSummary("The tenant's published branding for every screen: product name in English and Arabic, brand colours for light and dark mode, font, and the addresses of its images. Zelvo's default look until an administrator publishes one. Anyone.")
            .AllowAnonymousAccess()
            .Produces<PublicBrandingResponse>();
        branding.MapGet("/images/{slot}", ImageAsync)
            .WithName("GetBrandingImage")
            .WithSummary("A published branding image (LogoLight, LogoDark, Favicon, LoginImage) once scanned clean. Anyone. With draft=true, the draft's image for branding.manage (live preview).")
            .AllowAnonymousAccess();

        var settings = group.MapGroup("/settings/branding").WithTags("Branding").RequireTenant();
        settings.MapGet("/", SettingsAsync)
            .WithName("GetBrandingSettings")
            .WithSummary("Settings → Branding: the draft, the published branding, the contrast checks of the draft (WCAG 2.2 AA) and anything that stops publishing. Needs branding.manage.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<BrandingSettingsResponse>();
        settings.MapPut("/", UpdateDraftAsync)
            .WithName("UpdateBrandingDraft")
            .WithSummary("Changes the draft: product name (English and Arabic, up to 80 characters each), brand colours for light and dark mode (#rrggbb) and font. Nobody sees it until it is published. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<BrandingSettingsResponse>()
            .ProducesValidationProblem();
        settings.MapPost("/images/{slot}", UploadImageAsync)
            .WithName("UploadBrandingImage")
            .WithSummary("Puts an image in the draft: PNG, JPEG or WebP up to 2 MB (the favicon also ICO, up to 256 KB). The content must match the type; it is scanned for malware before it can be published. SVG is not accepted. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .DisableAntiforgery()
            .Produces<BrandingSettingsResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        settings.MapDelete("/images/{slot}", RemoveImageAsync)
            .WithName("RemoveBrandingImage")
            .WithSummary("Takes an image out of the draft (Zelvo's default is used). Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<BrandingSettingsResponse>();
        settings.MapPost("/publish", PublishAsync)
            .WithName("PublishBranding")
            .WithSummary("Publishes the draft for everyone: refused while a contrast check fails or an image is not scanned clean. Updates the sign-in pages too. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<BrandingSettingsResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);
        settings.MapPost("/discard", DiscardAsync)
            .WithName("DiscardBrandingDraft")
            .WithSummary("Throws the draft away and starts again from the published branding. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<BrandingSettingsResponse>();

        settings.MapGet("/domains", ListDomainsAsync)
            .WithName("ListCustomDomains")
            .WithSummary("Custom domains with what DNS must say for each: the TXT record that proves control and the CNAME to the installation. Needs branding.manage.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<IReadOnlyList<DomainResponse>>();
        settings.MapPost("/domains", AddDomainAsync)
            .WithName("AddCustomDomain")
            .WithSummary("Adds a custom domain such as support.entity.gov.ae, waiting for verification. A host belongs to one tenant of the installation. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<DomainResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        settings.MapPost("/domains/{id:guid}/verify", VerifyDomainAsync)
            .WithName("VerifyCustomDomain")
            .WithSummary("Looks up the TXT record _zelvo-verify.<host>. When it holds the domain's token, the domain is verified: the installation serves the tenant on it, the reverse proxy may get its certificate, and sign-in accepts it. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<DomainResponse>();
        settings.MapPut("/domains/{id:guid}/certificate", SetCertificateAsync)
            .WithName("SetCustomDomainCertificate")
            .WithSummary("Uploads the customer's own certificate for the domain (offline networks): PEM certificate chain and unencrypted PEM private key that belong together, valid now and for the host. The key is stored encrypted and never returned. Needs branding.manage; audited without the key.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<DomainResponse>()
            .ProducesValidationProblem();
        settings.MapDelete("/domains/{id:guid}/certificate", RemoveCertificateAsync)
            .WithName("RemoveCustomDomainCertificate")
            .WithSummary("Goes back to an automatic certificate (ACME) for the domain. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<DomainResponse>();
        settings.MapDelete("/domains/{id:guid}", RemoveDomainAsync)
            .WithName("RemoveCustomDomain")
            .WithSummary("Removes a custom domain; the installation stops answering on it. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces(StatusCodes.Status204NoContent);

        settings.MapGet("/email", SenderAsync)
            .WithName("GetSenderDomain")
            .WithSummary("The tenant's own email sender domain with the DNS records to publish (SPF, DKIM, DMARC) and the result of the last check. Needs branding.manage.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<SenderDomainResponse>();
        settings.MapPut("/email", SetSenderAsync)
            .WithName("SetSenderDomain")
            .WithSummary("Sets the sender address (local part and domain). A new domain gets a new DKIM key (RSA 2048, private key stored encrypted) and must be checked again. Until SPF and DKIM pass, emails keep the installation's sender. Needs branding.manage; audited without the key.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<SenderDomainResponse>()
            .ProducesValidationProblem();
        settings.MapPost("/email/check", CheckSenderAsync)
            .WithName("CheckSenderDomain")
            .WithSummary("The guided check: looks up SPF, the DKIM key and DMARC in DNS. With SPF and DKIM passed, notification emails go out from the tenant's address, signed with its DKIM key. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<SenderDomainResponse>();
        settings.MapDelete("/email", RemoveSenderAsync)
            .WithName("RemoveSenderDomain")
            .WithSummary("Goes back to the installation's sender. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/incidents/{id:guid}/exports", (Guid id, ExportRequest? request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken ct) =>
                RequestExportAsync(ExportKind.Incident, id, request, db, user, audit, ct))
            .WithName("ExportIncidentPdf").WithTags("Branding").RequireTenant()
            .RequirePerson()
            .WithSummary("Asks for a branded PDF of an incident in en or ar (default the caller's language). Staff get the staff view with internal notes; requesters their own incident's public view. The worker makes it; poll GET /exports/{id}. Audited.")
            .RequireAuthorization()
            .Produces<ExportResponse>(StatusCodes.Status202Accepted);
        group.MapPost("/service-requests/{id:guid}/exports", (Guid id, ExportRequest? request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken ct) =>
                RequestExportAsync(ExportKind.ServiceRequest, id, request, db, user, audit, ct))
            .WithName("ExportServiceRequestPdf").WithTags("Branding").RequireTenant()
            .RequirePerson()
            .WithSummary("Asks for a branded PDF of a service request, like incidents. Audited.")
            .RequireAuthorization()
            .Produces<ExportResponse>(StatusCodes.Status202Accepted);
        group.MapGet("/exports/{id:guid}", ExportAsync)
            .WithName("GetExport").WithTags("Branding").RequireTenant()
            .WithSummary("Where a PDF export is: Pending, Ready or Failed. Only the person who asked for it; kept for a day.")
            .RequireAuthorization()
            .Produces<ExportResponse>();
        group.MapGet("/exports/{id:guid}/file", ExportFileAsync)
            .WithName("DownloadExport").WithTags("Branding").RequireTenant()
            .WithSummary("Downloads a ready PDF export. Only the person who asked for it. Audited.")
            .RequireAuthorization();

        // The web app finds the tenant of a custom domain (no tenant is known yet).
        group.MapGet("/platform/hosts/{host}", HostAsync)
            .WithName("GetTenantOfHost").WithTags("Branding")
            .WithSummary("The tenant served on a verified custom domain: its identifier only. Anyone; 404 for unknown hosts.")
            .AllowAnonymousAccess()
            .Produces<HostResponse>();

        // The reverse proxy's questions (Caddy on-demand TLS and its certificate manager), never public.
        var tls = app.MapGroup("/internal/tls").ExcludeFromDescription();
        tls.MapGet("/ask", AskAsync).AllowAnonymous();
        tls.MapGet("/certificate", CertificateAsync).AllowAnonymous();
        return app;
    }

    // --- Published branding ---

    internal static async Task<IResult> PublishedAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.TenantBrandings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var value = BrandingValue.Read(row?.Published);
        return TypedResults.Ok(await PublicAsync(db, value, row?.PublishedAt, draft: false, cancellationToken));
    }

    internal static async Task<IResult> ImageAsync(string slot, bool? draft, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<BrandingSlot>(slot, ignoreCase: true, out var which) || !Enum.IsDefined(which))
        {
            return ImageNotFound();
        }

        var preview = draft == true;
        if (preview && !user.Has(Permissions.BrandingManage))
        {
            return ImageNotFound();
        }

        var row = await db.TenantBrandings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var value = BrandingValue.Read(preview ? row?.Draft : row?.Published);
        var image = await BrandingReader.ImageAsync(db, value, which, cancellationToken);
        if (image is null)
        {
            return ImageNotFound();
        }

        var etag = $"\"{image.Sha256[..16]}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = preview ? "private, no-store" : "public, max-age=300";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        if (http.Request.Headers.IfNoneMatch == etag)
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.File(image.Content, image.ContentType);
    }

    private static async Task<PublicBrandingResponse> PublicAsync(ZelvoDbContext db, BrandingValue value, DateTimeOffset? publishedAt, bool draft, CancellationToken cancellationToken)
    {
        var images = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var slot in Enum.GetValues<BrandingSlot>())
        {
            var image = await BrandingReader.ImageAsync(db, value, slot, cancellationToken);
            images[Camel(slot)] = image is null ? null : $"/api/v1/branding/images/{slot}?{(draft ? "draft=true&" : string.Empty)}v={image.Sha256[..12]}";
        }

        return new PublicBrandingResponse(LocalizedTextDto.From(value.ProductName), value.LightColour, value.DarkColour, value.Font, value.FontFamily, images, publishedAt);
    }

    // --- Settings ---

    internal static async Task<IResult> SettingsAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await SettingsViewAsync(db, await RowAsync(db, cancellationToken), cancellationToken));

    internal static async Task<IResult> UpdateDraftAsync(BrandingDraftRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var row = await RowAsync(db, cancellationToken);
        var draft = BrandingValue.Read(row.Draft);
        var name = request.ProductName is { } given ? new LocalizedText(given.En, given.Ar) : draft.ProductName;
        if ((name.En?.Length ?? 0) > BrandingValue.NameMaxLength || (name.Ar?.Length ?? 0) > BrandingValue.NameMaxLength)
        {
            return UserEndpoints.Validation("productName", $"The product name can have at most {BrandingValue.NameMaxLength} characters in each language.");
        }

        var light = request.LightColour?.Trim().ToLowerInvariant() ?? draft.LightColour;
        var dark = request.DarkColour?.Trim().ToLowerInvariant() ?? draft.DarkColour;
        if (!BrandingValue.IsColour(light) || !BrandingValue.IsColour(dark))
        {
            return UserEndpoints.Validation(BrandingValue.IsColour(light) ? "darkColour" : "lightColour", "Give the colour as #rrggbb, for example #0f5c8a.");
        }

        var font = request.Font ?? draft.Font;
        if (!BrandingFonts.Families.ContainsKey(font))
        {
            return UserEndpoints.Validation("font", $"Choose one of: {string.Join(", ", BrandingFonts.Families.Keys)}.");
        }

        row.Draft = (draft with { ProductName = name, LightColour = light, DarkColour = dark, Font = font }).Write();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await SettingsViewAsync(db, row, cancellationToken));
    }

    internal static async Task<IResult> UploadImageAsync(
        string slot, IFormFile? file, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit,
        IOptions<AttachmentScanningOptions> scanning, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<BrandingSlot>(slot, ignoreCase: true, out var which) || !Enum.IsDefined(which))
        {
            return ImageNotFound();
        }

        if (file is null || file.Length == 0)
        {
            return UserEndpoints.Validation("file", "Choose an image.");
        }

        var max = which == BrandingSlot.Favicon ? MaxFaviconBytes : MaxImageBytes;
        if (file.Length > max)
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "file-too-large", $"The image can be at most {max / 1024} KB.");
        }

        var name = FileTypes.CleanFileName(file.FileName);
        var type = FileTypes.Find(FileTypes.ExtensionOf(name));
        var allowed = which == BrandingSlot.Favicon ? [.. ImageTypes, "ico"] : ImageTypes;
        if (name.Length == 0 || type is null || !allowed.Contains(type.Extension, StringComparer.Ordinal))
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "file-type-not-allowed", $"Accepted image types: {string.Join(", ", allowed)}.");
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        if (!type.Check(content))
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "file-content-mismatch", "The file's content does not match its type.");
        }

        var row = await RowAsync(db, cancellationToken);
        var draft = BrandingValue.Read(row.Draft);
        var attachment = new TicketAttachment
        {
            BrandingSlot = which,
            FileName = name,
            ContentType = type.ContentType,
            Size = content.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            ScanStatus = scanning.Value.Enabled ? AttachmentScanStatus.Pending : AttachmentScanStatus.NotScanned,
            UploadedById = user.Account?.Id,
        };
        db.TicketAttachments.Add(attachment);
        store.Add(attachment.Id, content);
        var images = new Dictionary<BrandingSlot, Guid>(draft.Images) { [which] = attachment.Id };
        row.Draft = (draft with { Images = images }).Write();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "branding.image.uploaded", "tenant_branding", which.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["fileId"] = attachment.Id.ToString(), ["size"] = attachment.Size, ["sha256"] = attachment.Sha256 });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await SettingsViewAsync(db, row, cancellationToken));
    }

    internal static async Task<IResult> RemoveImageAsync(string slot, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<BrandingSlot>(slot, ignoreCase: true, out var which) || !Enum.IsDefined(which))
        {
            return ImageNotFound();
        }

        var row = await RowAsync(db, cancellationToken);
        var draft = BrandingValue.Read(row.Draft);
        var images = new Dictionary<BrandingSlot, Guid>(draft.Images);
        if (images.Remove(which))
        {
            row.Draft = (draft with { Images = images }).Write();
            row.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add(AuditCategory.Change, "branding.image.removed", "tenant_branding", which.ToString());
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await SettingsViewAsync(db, row, cancellationToken));
    }

    internal static async Task<IResult> PublishAsync(
        ZelvoDbContext db, CurrentUser user, IAuditLog audit, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity,
        IConfiguration configuration, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var row = await RowAsync(db, cancellationToken);
        var problems = await ProblemsAsync(db, BrandingValue.Read(row.Draft), cancellationToken);
        if (problems.Count > 0)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "branding-not-ready", $"The draft cannot be published yet: {string.Join(", ", problems)}.");
        }

        row.Published = row.Draft;
        row.PublishedAt = DateTimeOffset.UtcNow;
        row.PublishedById = user.Account?.Id;
        audit.Add(AuditCategory.Admin, "branding.published", "tenant_branding", tenant.TenantId.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await ApplyToSignInAsync(db, keycloak, identity.Value, WebUrl(configuration), tenant.Slug!, loggers, cancellationToken);
        return TypedResults.Ok(await SettingsViewAsync(db, row, cancellationToken));
    }

    internal static async Task<IResult> DiscardAsync(ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var row = await RowAsync(db, cancellationToken);
        row.Draft = row.Published ?? BrandingValue.Default.Write();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "branding.draft.discarded", "tenant_branding", null);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await SettingsViewAsync(db, row, cancellationToken));
    }

    /// <summary>
    /// Sign-in pages show the product name and the sign-in image (realm display name), and accept the
    /// tenant's verified domains as the web app's addresses. Keycloak being unreachable never undoes
    /// a publish or a verification: the next one applies it again.
    /// </summary>
    internal static async Task ApplyToSignInAsync(
        ZelvoDbContext db, IKeycloakAdmin keycloak, IdentityOptions identity, string webUrlSetting, string slug, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var value = BrandingValue.Read(await db.TenantBrandings.AsNoTracking().Select(b => b.Published).FirstOrDefaultAsync(cancellationToken));
        var webUrl = webUrlSetting.TrimEnd('/');
        var hosts = await db.TenantDomains.AsNoTracking().Where(d => d.Status == DomainStatus.Verified).Select(d => d.Host).OrderBy(h => h).ToListAsync(cancellationToken);
        var name = value.ProductName.En ?? value.ProductName.Ar;
        var html = WebUtility.HtmlEncode(name ?? slug);
        if (await BrandingReader.ImageAsync(db, value, BrandingSlot.LoginImage, cancellationToken) is { } image)
        {
            var source = $"{webUrl}/branding/{WebUtility.UrlEncode(slug)}/LoginImage?v={image.Sha256[..12]}";
            html = $"<img class=\"zelvo-login-image\" src=\"{WebUtility.HtmlEncode(source)}\" alt=\"\"><span>{html}</span>";
        }

        try
        {
            await keycloak.ApplyBrandingAsync(identity.RealmFor(slug), new RealmBranding(name ?? slug, html, [webUrl, .. hosts.Select(h => $"https://{h}")]), cancellationToken);
        }
        catch (Exception exception) when (exception is KeycloakAdminException or HttpRequestException)
        {
            SignInNotUpdated(loggers.CreateLogger("Zelvo.Branding"), exception, slug);
        }
    }

    private static string WebUrl(IConfiguration configuration) =>
        configuration[$"{RealmProvisioningOptions.Section}:WebUrl"] is { Length: > 0 } url ? url : new RealmProvisioningOptions().WebUrl;

    private static async Task<TenantBranding> RowAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.TenantBrandings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new TenantBranding { Draft = BrandingValue.Default.Write() };
            db.TenantBrandings.Add(row);
        }

        return row;
    }

    // What stops publishing: failed contrast checks and images not scanned clean.
    private static async Task<List<string>> ProblemsAsync(ZelvoDbContext db, BrandingValue draft, CancellationToken cancellationToken)
    {
        var problems = draft.Checks().Where(c => !c.Passed).Select(c => $"contrast:{c.Key}").ToList();
        var ids = draft.Images.Values.ToList();
        var files = await db.TicketAttachments.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.ScanStatus, cancellationToken);
        foreach (var (slot, id) in draft.Images)
        {
            var status = files.GetValueOrDefault(id, AttachmentScanStatus.Infected);
            if (status != AttachmentScanStatus.Clean && status != AttachmentScanStatus.NotScanned)
            {
                problems.Add($"image:{Camel(slot)}:{status}");
            }
        }

        return problems;
    }

    private static async Task<BrandingSettingsResponse> SettingsViewAsync(ZelvoDbContext db, TenantBranding row, CancellationToken cancellationToken)
    {
        var draft = BrandingValue.Read(row.Draft);
        var ids = draft.Images.Values.ToList();
        var files = await db.TicketAttachments.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        var images = draft.Images.ToDictionary(i => Camel(i.Key), i => files.TryGetValue(i.Value, out var f)
            ? new BrandingImageResponse(f.Id, f.FileName, f.ContentType, f.Size, f.ScanStatus, $"/api/v1/branding/images/{i.Key}?draft=true&v={f.Sha256[..12]}")
            : null, StringComparer.Ordinal);
        return new BrandingSettingsResponse(
            Draft(draft), row.Published is null ? null : Draft(BrandingValue.Read(row.Published)), row.PublishedAt, row.UpdatedAt,
            row.Published != row.Draft, [.. draft.Checks().Select(c => new ContrastCheckResponse(c.Key, c.Foreground, c.Background, c.Ratio, c.Passed))],
            await ProblemsAsync(db, draft, cancellationToken), images, [.. BrandingFonts.Families.Select(f => new FontResponse(f.Key, f.Value))]);
    }

    private static BrandingDraftResponse Draft(BrandingValue value) =>
        new(LocalizedTextDto.From(value.ProductName), value.LightColour, value.DarkColour, value.Font);

    // --- Custom domains ---

    internal static async Task<IResult> ListDomainsAsync(ZelvoDbContext db, IOptions<BrandingOptions> options, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.TenantDomains.AsNoTracking().OrderBy(d => d.Host).ToListAsync(cancellationToken)).Select(d => DomainResponse.From(d, options.Value)).ToList());

    internal static async Task<IResult> AddDomainAsync(DomainRequest request, ZelvoDbContext db, IAuditLog audit, IOptions<BrandingOptions> options, CancellationToken cancellationToken)
    {
        var host = HostNames.Normalize(request.Host);
        if (host is null)
        {
            return UserEndpoints.Validation("host", "Give a host name such as support.entity.gov.ae.");
        }

        if (options.Value.PublicHost is { } own && string.Equals(own, host, StringComparison.OrdinalIgnoreCase))
        {
            return UserEndpoints.Validation("host", "That is the installation's own address.");
        }

        if (await db.TenantDomains.CountAsync(cancellationToken) >= 10)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "too-many-domains", "A tenant has at most 10 custom domains.");
        }

        var domain = new TenantDomain { Host = host, VerificationToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) };
        db.TenantDomains.Add(domain);
        audit.Add(AuditCategory.Admin, "branding.domain.added", "tenant_domain", domain.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal) { ["host"] = host });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another tenant (invisible here) or this one already has it.
            return UserEndpoints.Validation("host", "This host is already in use in this installation.");
        }

        return TypedResults.Created($"/api/v1/settings/branding/domains/{domain.Id}", DomainResponse.From(domain, options.Value));
    }

    internal static async Task<IResult> VerifyDomainAsync(
        Guid id, ZelvoDbContext db, IDnsLookup dns, IAuditLog audit, IOptions<BrandingOptions> options, ITenantContext tenant, IKeycloakAdmin keycloak,
        IOptions<IdentityOptions> identity, IConfiguration configuration, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var domain = await db.TenantDomains.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (domain is null)
        {
            return DomainNotFound();
        }

        var records = await dns.TxtAsync($"_zelvo-verify.{domain.Host}", cancellationToken);
        var expected = $"zelvo-verify={domain.VerificationToken}";
        var now = DateTimeOffset.UtcNow;
        domain.LastCheckedAt = now;
        var wasVerified = domain.Status == DomainStatus.Verified;
        if (records.Any(r => string.Equals(r.Trim(), expected, StringComparison.Ordinal)))
        {
            domain.Status = DomainStatus.Verified;
            domain.VerifiedAt ??= now;
            domain.LastError = null;
        }
        else
        {
            domain.LastError = records.Count == 0 ? "txt-missing" : "txt-wrong";
        }

        audit.Add(AuditCategory.Admin, "branding.domain.checked", "tenant_domain", domain.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["host"] = domain.Host, ["verified"] = domain.Status == DomainStatus.Verified });
        await db.SaveChangesAsync(cancellationToken);
        if (!wasVerified && domain.Status == DomainStatus.Verified)
        {
            await ApplyToSignInAsync(db, keycloak, identity.Value, WebUrl(configuration), tenant.Slug!, loggers, cancellationToken);
        }

        return TypedResults.Ok(DomainResponse.From(domain, options.Value));
    }

    internal static async Task<IResult> SetCertificateAsync(
        Guid id, CertificateRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, IOptions<BrandingOptions> options, CancellationToken cancellationToken)
    {
        var domain = await db.TenantDomains.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (domain is null)
        {
            return DomainNotFound();
        }

        if (string.IsNullOrWhiteSpace(request.CertificatePem) || string.IsNullOrWhiteSpace(request.PrivateKeyPem)
            || request.CertificatePem.Length > TenantDomain.CertificateMaxLength || request.PrivateKeyPem.Length > TenantDomain.CertificateMaxLength)
        {
            return UserEndpoints.Validation("certificatePem", "Give the certificate chain and its private key in PEM format.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(request.CertificatePem, request.PrivateKeyPem);
        }
        catch (CryptographicException)
        {
            return UserEndpoints.Validation("privateKeyPem", "The certificate and the private key do not belong together, or one of them is not valid PEM (the key must not be encrypted).");
        }

        using (certificate)
        {
            var now = DateTimeOffset.UtcNow;
            if (certificate.NotBefore > now.UtcDateTime || certificate.NotAfter < now.UtcDateTime)
            {
                return UserEndpoints.Validation("certificatePem", "The certificate is not valid now.");
            }

            if (!certificate.MatchesHostname(domain.Host))
            {
                return UserEndpoints.Validation("certificatePem", $"The certificate is not for {domain.Host}.");
            }

            domain.CertificatePem = request.CertificatePem.Trim();
            domain.PrivateKeyCiphertext = await secrets.ProtectAsync(CertificatePurpose(domain.Id), request.PrivateKeyPem.Trim(), cancellationToken);
            domain.CertificateExpiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            domain.CertificateMode = CertificateMode.Uploaded;
            audit.Add(AuditCategory.Admin, "branding.domain.certificate-uploaded", "tenant_domain", domain.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["host"] = domain.Host, ["thumbprint"] = certificate.Thumbprint, ["expiresAt"] = domain.CertificateExpiresAt });
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(DomainResponse.From(domain, options.Value));
    }

    internal static async Task<IResult> RemoveCertificateAsync(Guid id, ZelvoDbContext db, IAuditLog audit, IOptions<BrandingOptions> options, CancellationToken cancellationToken)
    {
        var domain = await db.TenantDomains.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (domain is null)
        {
            return DomainNotFound();
        }

        domain.CertificateMode = CertificateMode.Automatic;
        domain.CertificatePem = null;
        domain.PrivateKeyCiphertext = null;
        domain.CertificateExpiresAt = null;
        audit.Add(AuditCategory.Admin, "branding.domain.certificate-removed", "tenant_domain", domain.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(DomainResponse.From(domain, options.Value));
    }

    internal static async Task<IResult> RemoveDomainAsync(Guid id, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var domain = await db.TenantDomains.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (domain is null)
        {
            return DomainNotFound();
        }

        db.TenantDomains.Remove(domain);
        audit.Add(AuditCategory.Admin, "branding.domain.removed", "tenant_domain", domain.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal) { ["host"] = domain.Host });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Secret purpose of an uploaded certificate's private key.</summary>
    internal static string CertificatePurpose(Guid domainId) => $"branding.domain:{domainId:N}.privateKey";

    internal static async Task<IResult> HostAsync(string host, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await TenantOfHostAsync(db, host, cancellationToken) is { } tenant
            ? TypedResults.Ok(new HostResponse(tenant.Slug))
            : ApiProblem.Create(StatusCodes.Status404NotFound, "host-not-found", "No tenant is served on this host.");

    /// <summary>The tenant of a verified host, through the SECURITY DEFINER function (no tenant is set yet).</summary>
    internal static async Task<(Guid Id, string Slug)?> TenantOfHostAsync(ZelvoDbContext db, string? host, CancellationToken cancellationToken)
    {
        if (HostNames.Normalize(host) is not { } normalized)
        {
            return null;
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_tenant_for_host(@host)", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("host", normalized);
        (Guid, string)? found;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            found = await reader.ReadAsync(cancellationToken) ? (reader.GetGuid(0), reader.GetString(1)) : null;
        }

        // The tenant setting is applied when a connection opens: close it so a tenant set next applies.
        await db.Database.CloseConnectionAsync();
        return found;
    }

    // The reverse proxy may get a certificate only for hosts a tenant verified (on-demand TLS "ask").
    internal static async Task<IResult> AskAsync(string? domain, string? token, ZelvoDbContext db, IOptions<BrandingOptions> options, CancellationToken cancellationToken) =>
        !TokenMatches(options.Value, token) ? Results.NotFound()
        : await TenantOfHostAsync(db, domain, cancellationToken) is not null ? Results.Ok() : Results.NotFound();

    // An uploaded certificate for the host in the TLS handshake (Caddy's "http" certificate manager); 204 lets the proxy use its own.
    internal static async Task<IResult> CertificateAsync(
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "server_name")] string? serverName, string? token, ZelvoDbContext db, TenantContext tenantContext,
        ISecretProtector secrets, IOptions<BrandingOptions> options, CancellationToken cancellationToken)
    {
        if (!TokenMatches(options.Value, token))
        {
            return Results.NotFound();
        }

        if (await TenantOfHostAsync(db, serverName, cancellationToken) is not { } tenant)
        {
            return Results.NoContent();
        }

        tenantContext.SetTenant(tenant.Id, tenant.Slug);
        var host = HostNames.Normalize(serverName)!;
        var domain = await db.TenantDomains.AsNoTracking().SingleOrDefaultAsync(d => d.Host == host && d.CertificateMode == CertificateMode.Uploaded, cancellationToken);
        if (domain?.CertificatePem is null || domain.PrivateKeyCiphertext is null)
        {
            return Results.NoContent();
        }

        var key = await secrets.UnprotectAsync(CertificatePurpose(domain.Id), domain.PrivateKeyCiphertext, cancellationToken);
        return Results.Text($"{domain.CertificatePem}\n{key}\n", "application/x-pem-file");
    }

    private static bool TokenMatches(BrandingOptions options, string? token) =>
        options.TlsToken is { Length: >= 16 } expected && token is not null
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(token));

    // --- Sender domain ---

    internal static async Task<IResult> SenderAsync(ZelvoDbContext db, IOptions<BrandingOptions> options, CancellationToken cancellationToken) =>
        await db.SenderDomains.AsNoTracking().FirstOrDefaultAsync(cancellationToken) is { } sender
            ? TypedResults.Ok(SenderDomainResponse.From(sender, options.Value))
            : ApiProblem.Create(StatusCodes.Status404NotFound, "sender-domain-not-set", "The installation's sender is used.");

    internal static async Task<IResult> SetSenderAsync(
        SenderDomainRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, IOptions<BrandingOptions> options, CancellationToken cancellationToken)
    {
        var domain = HostNames.Normalize(request.Domain);
        if (domain is null)
        {
            return UserEndpoints.Validation("domain", "Give a domain such as entity.gov.ae.");
        }

        var local = (request.LocalPart ?? "noreply").Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(local, "^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return UserEndpoints.Validation("localPart", "Use letters, digits, dots, hyphens or underscores, for example noreply.");
        }

        var sender = await db.SenderDomains.FirstOrDefaultAsync(cancellationToken);
        if (sender is null || sender.Domain != domain)
        {
            using var rsa = RSA.Create(2048);
            var selector = $"zelvo{DateTimeOffset.UtcNow:yyyyMM}";
            var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
            var privateKey = await secrets.ProtectAsync(SenderDomainKeys.Purpose, rsa.ExportPkcs8PrivateKeyPem(), cancellationToken);
            if (sender is null)
            {
                sender = new SenderDomain { Domain = domain, LocalPart = local, DkimSelector = selector, DkimPublicKey = publicKey, DkimPrivateKeyCiphertext = privateKey };
                db.SenderDomains.Add(sender);
            }
            else
            {
                sender.Domain = domain;
                sender.DkimSelector = selector;
                sender.DkimPublicKey = publicKey;
                sender.DkimPrivateKeyCiphertext = privateKey;
            }

            sender.Spf = sender.Dkim = sender.Dmarc = DnsCheck.Unknown;
            sender.Verified = false;
            sender.LastCheckedAt = null;
        }

        sender.LocalPart = local;
        audit.Add(AuditCategory.Admin, "branding.sender-domain.set", "sender_domain", null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["address"] = $"{local}@{domain}" });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(SenderDomainResponse.From(sender, options.Value));
    }

    internal static async Task<IResult> CheckSenderAsync(ZelvoDbContext db, IDnsLookup dns, IAuditLog audit, IOptions<BrandingOptions> options, CancellationToken cancellationToken)
    {
        var sender = await db.SenderDomains.FirstOrDefaultAsync(cancellationToken);
        if (sender is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "sender-domain-not-set", "The installation's sender is used.");
        }

        var spf = (await dns.TxtAsync(sender.Domain, cancellationToken)).Where(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
        sender.Spf = spf.Count == 0 ? DnsCheck.Missing
            : options.Value.SpfInclude is { Length: > 0 } include && !spf.Any(r => r.Split(' ').Contains(include, StringComparer.OrdinalIgnoreCase)) ? DnsCheck.Wrong
            : DnsCheck.Passed;
        var dkim = await dns.TxtAsync($"{sender.DkimSelector}._domainkey.{sender.Domain}", cancellationToken);
        sender.Dkim = dkim.Count == 0 ? DnsCheck.Missing
            : dkim.Any(r => r.Replace(" ", string.Empty, StringComparison.Ordinal).Contains($"p={sender.DkimPublicKey}", StringComparison.Ordinal)) ? DnsCheck.Passed
            : DnsCheck.Wrong;
        var dmarc = (await dns.TxtAsync($"_dmarc.{sender.Domain}", cancellationToken)).Where(r => r.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList();
        sender.Dmarc = dmarc.Count == 0 ? DnsCheck.Missing : DnsCheck.Passed;
        sender.Verified = sender.Spf == DnsCheck.Passed && sender.Dkim == DnsCheck.Passed;
        sender.LastCheckedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "branding.sender-domain.checked", "sender_domain", null, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["spf"] = sender.Spf.ToString(), ["dkim"] = sender.Dkim.ToString(), ["dmarc"] = sender.Dmarc.ToString(), ["verified"] = sender.Verified,
        });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(SenderDomainResponse.From(sender, options.Value));
    }

    internal static async Task<IResult> RemoveSenderAsync(ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (await db.SenderDomains.FirstOrDefaultAsync(cancellationToken) is { } sender)
        {
            db.SenderDomains.Remove(sender);
            audit.Add(AuditCategory.Admin, "branding.sender-domain.removed", "sender_domain", null);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    // --- PDF exports ---

    internal static async Task<IResult> RequestExportAsync(ExportKind kind, Guid id, ExportRequest? request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = kind == ExportKind.Incident
            ? await Incidents.IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken)
            : await Catalog.ServiceRequestEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, kind == ExportKind.Incident ? "incident-not-found" : "request-not-found", "It does not exist or you cannot see it.");
        }

        var me = user.Account!;
        var staff = user.Has(kind == ExportKind.Incident ? Permissions.IncidentsRead : Permissions.RequestsRead);
        var language = request?.Language is LocalizedText.English or LocalizedText.Arabic ? request.Language
            : me.PreferredLanguage is LocalizedText.English or LocalizedText.Arabic ? me.PreferredLanguage : LocalizedText.English;
        if (await db.DocumentExports.CountAsync(e => e.RequestedById == me.Id && e.Status == ExportStatus.Pending, cancellationToken) >= 5)
        {
            return ApiProblem.Create(StatusCodes.Status429TooManyRequests, "too-many-exports", "Wait for your other exports to finish.");
        }

        var export = new DocumentExport { Kind = kind, EntityId = ticket.Id, Language = language, StaffView = staff, RequestedById = me.Id };
        db.DocumentExports.Add(export);
        audit.Add(AuditCategory.View, "export.requested", "ticket", ticket.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["exportId"] = export.Id.ToString(), ["language"] = language, ["staffView"] = staff });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted($"/api/v1/exports/{export.Id}", ExportResponse.From(export));
    }

    internal static async Task<IResult> ExportAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken) =>
        await OwnExportAsync(db, user, id, cancellationToken) is { } export ? TypedResults.Ok(ExportResponse.From(export)) : ExportNotFound();

    internal static async Task<IResult> ExportFileAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var export = await OwnExportAsync(db, user, id, cancellationToken);
        if (export is not { Status: ExportStatus.Ready, Content: not null })
        {
            return ExportNotFound();
        }

        audit.Add(AuditCategory.View, "export.downloaded", "ticket", export.EntityId.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal) { ["exportId"] = export.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.File(export.Content, "application/pdf", export.FileName);
    }

    private static Task<DocumentExport?> OwnExportAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken) =>
        user.Account is { } me
            ? db.DocumentExports.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.RequestedById == me.Id, cancellationToken)
            : Task.FromResult<DocumentExport?>(null);

    private static string Camel(BrandingSlot slot) => char.ToLowerInvariant(slot.ToString()[0]) + slot.ToString()[1..];

    private static IResult ImageNotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "image-not-found", "There is no such branding image.");

    private static IResult DomainNotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "domain-not-found", "There is no such custom domain.");

    private static IResult ExportNotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "export-not-found", "There is no such export, or it is not ready.");
}

/// <summary>The published branding.</summary>
internal sealed record PublicBrandingResponse(
    LocalizedTextDto ProductName, string LightColour, string DarkColour, string Font, string FontFamily, IReadOnlyDictionary<string, string?> Images, DateTimeOffset? PublishedAt);

/// <summary>Draft or published values.</summary>
internal sealed record BrandingDraftResponse(LocalizedTextDto ProductName, string LightColour, string DarkColour, string Font);

/// <summary>A contrast check.</summary>
internal sealed record ContrastCheckResponse(string Key, string Foreground, string Background, double Ratio, bool Passed);

/// <summary>An image of the draft.</summary>
internal sealed record BrandingImageResponse(Guid Id, string FileName, string ContentType, long Size, AttachmentScanStatus ScanStatus, string Url);

/// <summary>A font that can be chosen.</summary>
internal sealed record FontResponse(string Key, string Family);

/// <summary>Settings → Branding.</summary>
/// <param name="Unpublished">The draft differs from what is published.</param>
/// <param name="Problems">What stops publishing (failed contrast checks, images not scanned clean).</param>
internal sealed record BrandingSettingsResponse(
    BrandingDraftResponse Draft, BrandingDraftResponse? Published, DateTimeOffset? PublishedAt, DateTimeOffset UpdatedAt, bool Unpublished,
    IReadOnlyList<ContrastCheckResponse> Checks, IReadOnlyList<string> Problems, IReadOnlyDictionary<string, BrandingImageResponse?> Images, IReadOnlyList<FontResponse> Fonts);

/// <summary>A DNS record to publish.</summary>
internal sealed record DnsRecordResponse(string Type, string Name, string Value, DnsCheck Status);

/// <summary>A custom domain.</summary>
internal sealed record DomainResponse(
    Guid Id, string Host, DomainStatus Status, DateTimeOffset? VerifiedAt, DateTimeOffset? LastCheckedAt, string? LastError, CertificateMode CertificateMode,
    DateTimeOffset? CertificateExpiresAt, IReadOnlyList<DnsRecordResponse> Records)
{
    public static DomainResponse From(TenantDomain d, BrandingOptions options) => new(
        d.Id, d.Host, d.Status, d.VerifiedAt, d.LastCheckedAt, d.LastError, d.CertificateMode, d.CertificateExpiresAt,
        [
            new("TXT", $"_zelvo-verify.{d.Host}", $"zelvo-verify={d.VerificationToken}", d.Status == DomainStatus.Verified ? DnsCheck.Passed : d.LastError is null ? DnsCheck.Unknown : DnsCheck.Missing),
            .. options.PublicHost is { Length: > 0 } target ? [new DnsRecordResponse("CNAME", d.Host, target, DnsCheck.Unknown)] : Array.Empty<DnsRecordResponse>(),
        ]);
}

/// <summary>The sender domain with its DNS records.</summary>
internal sealed record SenderDomainResponse(
    string Domain, string LocalPart, string Address, bool Verified, DateTimeOffset? LastCheckedAt, IReadOnlyList<DnsRecordResponse> Records)
{
    public static SenderDomainResponse From(SenderDomain s, BrandingOptions options) => new(
        s.Domain, s.LocalPart, $"{s.LocalPart}@{s.Domain}", s.Verified, s.LastCheckedAt,
        [
            new("TXT", s.Domain, options.SpfInclude is { Length: > 0 } include ? $"v=spf1 {include} ~all" : "v=spf1 a mx ~all", s.Spf),
            new("TXT", $"{s.DkimSelector}._domainkey.{s.Domain}", $"v=DKIM1; k=rsa; p={s.DkimPublicKey}", s.Dkim),
            new("TXT", $"_dmarc.{s.Domain}", $"v=DMARC1; p=quarantine; rua=mailto:dmarc@{s.Domain}", s.Dmarc),
        ]);
}

/// <summary>A PDF export.</summary>
internal sealed record ExportResponse(Guid Id, ExportKind Kind, Guid EntityId, string Language, ExportStatus Status, string? FileName, string? Error, DateTimeOffset CreatedAt)
{
    public static ExportResponse From(DocumentExport e) => new(e.Id, e.Kind, e.EntityId, e.Language, e.Status, e.FileName, e.Status == ExportStatus.Failed ? e.Error : null, e.CreatedAt);
}

/// <summary>The tenant of a host.</summary>
internal sealed record HostResponse(string Tenant);

/// <summary>Draft values; leave one out to keep it.</summary>
internal sealed class BrandingDraftRequest
{
    public LocalizedTextInput? ProductName { get; init; }

    public string? LightColour { get; init; }

    public string? DarkColour { get; init; }

    public string? Font { get; init; }
}

/// <summary>A custom domain to add.</summary>
internal sealed class DomainRequest
{
    public string? Host { get; init; }
}

/// <summary>An uploaded certificate.</summary>
internal sealed class CertificateRequest
{
    public string? CertificatePem { get; init; }

    public string? PrivateKeyPem { get; init; }
}

/// <summary>The sender address.</summary>
internal sealed class SenderDomainRequest
{
    public string? Domain { get; init; }

    public string? LocalPart { get; init; }
}

/// <summary>An export's language.</summary>
internal sealed class ExportRequest
{
    public string? Language { get; init; }
}

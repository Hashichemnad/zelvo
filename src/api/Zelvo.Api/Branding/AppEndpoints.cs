using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Branding;
using Zelvo.Tickets.Branding;

namespace Zelvo.Api.Branding;

/// <summary>Tenant settings of the installable web app (module 19, task J1).</summary>
internal static class AppSettings
{
    /// <summary>
    /// Decision 13 (2026-11-26): whether the installed app keeps the requester's last-viewed tickets
    /// and unsent new tickets and replies on the device. On by default; turned off, the app still
    /// opens offline but keeps nothing.
    /// </summary>
    public static SettingDefinition<bool> OfflineData { get; } = new("app.offlineData", true, Permissions.BrandingManage);
}

/// <summary>
/// The installable web app (module 19, task J1): the tenant's app icon in the sizes phones and
/// computers need (made by the server from the published square PNG), what the web app needs for
/// its manifest and service worker, and Settings → Branding → Installable app.
/// </summary>
internal static class AppEndpoints
{
    private static readonly TimeSpan IconCacheTime = TimeSpan.FromHours(1);

    public static RouteGroupBuilder MapAppEndpoints(this RouteGroupBuilder group)
    {
        var app = group.MapGroup("/app").WithTags("Installable app").RequireTenant();
        app.MapGet("/", AppAsync)
            .WithName("GetInstallableApp")
            .WithSummary("What the installable web app needs: whether it may keep data on the device for offline use (the requester's last-viewed tickets, unsent new tickets and replies) and the addresses of the app icon's sizes, or null while the tenant has no published app icon. Anyone.")
            .AllowAnonymousAccess()
            .Produces<AppResponse>();

        group.MapGet("/branding/app-icons/{name}", IconAsync)
            .WithName("GetAppIcon").WithTags("Installable app").RequireTenant()
            .WithSummary("A size of the published app icon: any-192, any-512 (as uploaded, transparency kept), maskable-192, maskable-512 (inside the safe zone on a full background) or apple-180 (iOS, on a full background). Made by the server from the square PNG. Anyone; 404 while there is no published app icon.")
            .AllowAnonymousAccess();

        var settings = group.MapGroup("/settings/app").WithTags("Installable app").RequireTenant();
        settings.MapGet("/", SettingsAsync)
            .WithName("GetAppSettings")
            .WithSummary("Settings → Branding → Installable app: whether the app keeps data on the device for offline use. Needs branding.manage.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<AppSettingsResponse>();
        settings.MapPut("/", UpdateSettingsAsync)
            .WithName("UpdateAppSettings")
            .WithSummary("Turns offline data on or off. Off: the installed app still opens without a connection but keeps no tickets and no unsent work on the device, and clears what it kept. Needs branding.manage; audited.")
            .RequirePermission(Permissions.BrandingManage)
            .Produces<AppSettingsResponse>();
        return group;
    }

    internal static async Task<IResult> AppAsync(ZelvoDbContext db, TenantSettingsStore store, CancellationToken cancellationToken)
    {
        var branding = await BrandingReader.PublishedAsync(db, cancellationToken);
        var icon = await BrandingReader.ImageAsync(db, branding, BrandingSlot.AppIcon, cancellationToken);
        var icons = icon is null
            ? null
            : AppIcons.Renditions.ToDictionary(r => r.Name, r => $"/api/v1/branding/app-icons/{r.Name}?v={icon.Sha256[..12]}", StringComparer.Ordinal);
        return TypedResults.Ok(new AppResponse(await store.GetAsync(AppSettings.OfflineData, cancellationToken), icons));
    }

    internal static async Task<IResult> IconAsync(string name, HttpContext http, ZelvoDbContext db, IMemoryCache cache, CancellationToken cancellationToken)
    {
        var rendition = AppIcons.Find(name);
        var branding = await BrandingReader.PublishedAsync(db, cancellationToken);
        var icon = rendition is null ? null : await BrandingReader.ImageAsync(db, branding, BrandingSlot.AppIcon, cancellationToken);
        if (rendition is null || icon is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "app-icon-not-found", "There is no such app icon.");
        }

        var etag = $"\"{icon.Sha256[..16]}-{rendition.Name}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "public, max-age=300";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        if (http.Request.Headers.IfNoneMatch == etag)
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        // The image's hash is in the key, so a new icon never gets an old size; the content is the
        // published, clean image of the request's tenant (row-level security).
        var background = Rgb(BrandingValue.LightSurface);
        var bytes = await cache.GetOrCreateAsync($"zelvo:app-icon:{icon.Sha256}:{rendition.Name}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = IconCacheTime;
            entry.Size = 1;
            return Task.FromResult(AppIcons.Render(icon.Content, rendition, background));
        });
        return TypedResults.File(bytes!, "image/png");
    }

    internal static async Task<IResult> SettingsAsync(TenantSettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(new AppSettingsResponse(await store.GetAsync(AppSettings.OfflineData, cancellationToken)));

    internal static async Task<IResult> UpdateSettingsAsync(AppSettingsRequest request, TenantSettingsStore store, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (request.OfflineData is not { } offline)
        {
            return UserEndpoints.Validation("offlineData", "Say whether the app may keep data on the device.");
        }

        await store.SetAsync(AppSettings.OfflineData, offline, cancellationToken);
        audit.Add(AuditCategory.Admin, "app.settings.changed", "settings", "app",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["offlineData"] = offline });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new AppSettingsResponse(offline));
    }

    private static (byte R, byte G, byte B) Rgb(string colour) => (
        byte.Parse(colour.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(colour.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(colour.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}

/// <summary>What the installable web app needs.</summary>
/// <param name="OfflineData">Whether the app may keep the requester's last-viewed tickets and unsent work on the device.</param>
/// <param name="Icons">Addresses of the app icon's sizes by name (any-192, any-512, maskable-192, maskable-512, apple-180), or null for Zelvo's own icon.</param>
internal sealed record AppResponse(bool OfflineData, IReadOnlyDictionary<string, string>? Icons);

/// <summary>Settings → Branding → Installable app.</summary>
/// <param name="OfflineData">Whether the app may keep data on the device.</param>
internal sealed record AppSettingsResponse(bool OfflineData);

/// <summary>Changes Settings → Branding → Installable app.</summary>
/// <param name="OfflineData">Whether the app may keep data on the device.</param>
internal sealed record AppSettingsRequest(bool? OfflineData);

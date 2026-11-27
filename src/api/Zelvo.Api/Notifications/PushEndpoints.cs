using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Notifications;
using Zelvo.Notifications;
using Zelvo.Notifications.WebPush;

namespace Zelvo.Api.Notifications;

/// <summary>
/// Web push (module 19, task J2): each person's devices that receive notifications, the tenant's
/// public key the browser subscribes with, and a test notification. Push needs outgoing internet
/// to the browsers' push services (product owner decision 2026-11-26); installations without it
/// set <c>Notifications:Push:Enabled</c> to false and these endpoints say push is not available.
/// </summary>
internal static class PushEndpoints
{
    /// <summary>Devices one person may have; adding another removes the one unused longest.</summary>
    public const int MaxDevices = 20;

    public static RouteGroupBuilder MapPushEndpoints(this RouteGroupBuilder group)
    {
        var push = group.MapGroup("/me/push").WithTags("Notifications").RequireTenant().RequirePerson();

        push.MapGet("/", StatusAsync)
            .WithName("GetMyWebPush")
            .WithSummary("Whether this installation offers web push, the public key (VAPID, RFC 8292) browsers subscribe with, and your devices. The tenant's key pair is made the first time it is asked for.")
            .Produces<PushStatusResponse>();

        push.MapPost("/subscriptions", SubscribeAsync)
            .WithName("AddMyPushDevice")
            .WithSummary("Adds this browser or installed app as one of your devices (its push subscription). Only addresses of the known push services are accepted. A subscription already held by someone else on this device moves to you. Audited.")
            .Produces<PushDeviceResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        push.MapDelete("/subscriptions/{id:guid}", RemoveAsync)
            .WithName("RemoveMyPushDevice")
            .WithSummary("Removes one of your devices; it gets no more notifications. Audited.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        push.MapPost("/subscriptions/remove", RemoveByEndpointAsync)
            .WithName("RemoveMyPushDeviceByAddress")
            .WithSummary("Removes your device with this push address (signing out on the device). Answers 204 whether or not it was there.")
            .Produces(StatusCodes.Status204NoContent);

        push.MapPost("/test", TestAsync)
            .WithName("SendMyTestPush")
            .WithSummary("Sends a test notification to your devices (or one of them) in your language; the worker sends it within seconds. Audited.")
            .Produces<PushTestResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    // The key store (and the secrets it needs) is resolved only when push is offered.
    internal static async Task<IResult> StatusAsync(CurrentUser user, ZelvoDbContext db, NotificationOptions options, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!options.Push.Enabled)
        {
            return TypedResults.Ok(new PushStatusResponse(false, null, []));
        }

        var me = user.Account!.Id;
        var devices = await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == me).OrderByDescending(s => s.CreatedAt)
            .Select(s => new PushDeviceResponse(s.Id, s.DeviceName, s.CreatedAt, s.LastSuccessAt)).ToListAsync(cancellationToken);
        var keys = services.GetRequiredService<WebPushKeys>();
        return TypedResults.Ok(new PushStatusResponse(true, await keys.PublicKeyAsync(cancellationToken), devices));
    }

    internal static async Task<IResult> SubscribeAsync(
        PushSubscriptionRequest request, HttpContext http, CurrentUser user, ZelvoDbContext db, NotificationOptions options, CancellationToken cancellationToken)
    {
        if (!options.Push.Enabled)
        {
            return PushNotAvailable();
        }

        var endpoint = request.Endpoint?.Trim();
        if (endpoint is null || endpoint.Length > PushSubscription.EndpointMaxLength || !options.Push.IsAllowedEndpoint(endpoint))
        {
            return UserEndpoints.Validation("endpoint", "This is not the address of a known push service.");
        }

        if (!WebPushCrypto.AreValidSubscriptionKeys(request.P256dh, request.Auth))
        {
            return UserEndpoints.Validation("keys", "The browser's keys are not a P-256 public key and a 16-byte secret.");
        }

        // Makes the tenant's key pair if this is the first device.
        await http.RequestServices.GetRequiredService<WebPushKeys>().PublicKeyAsync(cancellationToken);
        var me = user.Account!.Id;
        var name = DeviceName(request.DeviceName, http.Request.Headers.UserAgent.ToString());
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);
        if (existing is not null && existing.UserId != me)
        {
            // Someone else signed in on this device before: their notifications stop reaching it.
            db.PushSubscriptions.Remove(existing);
            existing = null;
        }

        if (existing is null)
        {
            var mine = await db.PushSubscriptions.Where(s => s.UserId == me)
                .OrderBy(s => s.LastSuccessAt ?? s.CreatedAt).ToListAsync(cancellationToken);
            foreach (var old in mine.Take(Math.Max(0, mine.Count - MaxDevices + 1)))
            {
                db.PushSubscriptions.Remove(old);
            }

            existing = new PushSubscription { UserId = me, Endpoint = endpoint, P256dh = request.P256dh!, Auth = request.Auth!, DeviceName = name };
            db.PushSubscriptions.Add(existing);
        }
        else
        {
            existing.P256dh = request.P256dh!;
            existing.Auth = request.Auth!;
            existing.DeviceName = name;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new PushDeviceResponse(existing.Id, existing.DeviceName, existing.CreatedAt, existing.LastSuccessAt));
    }

    internal static async Task<IResult> RemoveAsync(Guid id, CurrentUser user, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var device = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == me, cancellationToken);
        if (device is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "push-device-not-found", "You have no device with this id.");
        }

        db.PushSubscriptions.Remove(device);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> RemoveByEndpointAsync(PushEndpointRequest request, CurrentUser user, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        if (request.Endpoint is { Length: > 0 and <= PushSubscription.EndpointMaxLength } endpoint
            && await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint && s.UserId == me, cancellationToken) is { } device)
        {
            db.PushSubscriptions.Remove(device);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    internal static async Task<IResult> TestAsync(
        PushTestRequest? request, CurrentUser user, ZelvoDbContext db, NotificationOptions options, NotificationComposer composer, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        if (!options.Push.Enabled)
        {
            return PushNotAvailable();
        }

        var account = user.Account!;
        var devices = await db.PushSubscriptions.AsNoTracking()
            .Where(s => s.UserId == account.Id && (request == null || request.DeviceId == null || s.Id == request.DeviceId))
            .Select(s => s.Id).ToListAsync(cancellationToken);
        if (devices.Count == 0)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "no-push-devices", "You have no device that receives notifications. Turn them on on this device first.");
        }

        var format = await composer.FormatAsync(account, cancellationToken);
        var terms = NotificationTexts.For(format.Language).Terms;
        var now = DateTimeOffset.UtcNow;
        foreach (var device in devices)
        {
            db.PushDeliveries.Add(new PushDelivery
            {
                SubscriptionId = device,
                RecipientId = account.Id,
                EventType = "push.test",
                Language = format.Language,
                Title = terms["push.test.title"],
                Body = terms["push.test.body"],
                Url = $"/{format.Language}/account#notifications",
                Tag = "push.test",
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }

        audit.Add(AuditCategory.Admin, "notifications.push.test", "user", account.Id.ToString(), new Dictionary<string, object?> { ["devices"] = devices.Count });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted("/api/v1/me/push", new PushTestResponse(devices.Count));
    }

    /// <summary>
    /// A name the person recognises: what the page sent (trimmed), else the browser and system
    /// from the User-Agent, for example "Chrome on Android".
    /// </summary>
    internal static string DeviceName(string? given, string? userAgent)
    {
        if (given?.Trim() is { Length: > 0 } name)
        {
            var clean = new string([.. name.Where(c => !char.IsControl(c))]);
            return clean.Length <= PushSubscription.DeviceNameMaxLength ? clean : clean[..PushSubscription.DeviceNameMaxLength];
        }

        var agent = userAgent ?? string.Empty;
        var browser = agent.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : agent.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : agent.Contains("Chrome/", StringComparison.Ordinal) || agent.Contains("CriOS/", StringComparison.Ordinal) ? "Chrome"
            : agent.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : "Browser";
        var system = agent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : agent.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : agent.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : agent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : agent.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS"
            : agent.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : null;
        return system is null ? browser : $"{browser} on {system}";
    }

    private static IResult PushNotAvailable() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, "push-not-available", "This installation does not offer web push (it needs outgoing internet to the browsers' push services).");
}

/// <summary>Web push for you.</summary>
/// <param name="Available">Whether this installation offers web push.</param>
/// <param name="PublicKey">The key browsers subscribe with (base64url), when available.</param>
/// <param name="Devices">Your devices.</param>
internal sealed record PushStatusResponse(bool Available, string? PublicKey, IReadOnlyList<PushDeviceResponse> Devices);

/// <summary>One of your devices.</summary>
/// <param name="Id">Id.</param>
/// <param name="DeviceName">Name, for example "Chrome on Android".</param>
/// <param name="CreatedAt">When it was added.</param>
/// <param name="LastSuccessAt">When a notification last reached its push service.</param>
internal sealed record PushDeviceResponse(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset? LastSuccessAt);

/// <summary>How many test notifications were queued.</summary>
/// <param name="Devices">Devices.</param>
internal sealed record PushTestResponse(int Devices);

/// <summary>A browser's push subscription (<c>PushSubscription.toJSON()</c>).</summary>
public sealed class PushSubscriptionRequest
{
    /// <summary>The push service's address.</summary>
    [Required]
    public string? Endpoint { get; init; }

    /// <summary>The browser's public key (<c>keys.p256dh</c>), base64url.</summary>
    [Required]
    public string? P256dh { get; init; }

    /// <summary>The browser's auth secret (<c>keys.auth</c>), base64url.</summary>
    [Required]
    public string? Auth { get; init; }

    /// <summary>A name for the device; from the browser when left out.</summary>
    public string? DeviceName { get; init; }
}

/// <summary>A push address.</summary>
public sealed class PushEndpointRequest
{
    /// <summary>The push service's address of the device.</summary>
    public string? Endpoint { get; init; }
}

/// <summary>Which device gets the test notification.</summary>
public sealed class PushTestRequest
{
    /// <summary>One device; all of yours when left out.</summary>
    public Guid? DeviceId { get; init; }
}

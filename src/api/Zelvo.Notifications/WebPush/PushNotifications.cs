using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Notifications;
using Zelvo.Integrations;
using Zelvo.Secrets;

namespace Zelvo.Notifications.WebPush;

/// <summary>
/// The tenant's VAPID key pair (task J2): made the first time it is needed, the private key
/// encrypted with <c>Zelvo.Secrets</c>, never replaced while browsers hold subscriptions to it.
/// </summary>
public sealed class WebPushKeys(ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit)
{
    /// <summary>Purpose of the encrypted private key.</summary>
    public const string Purpose = "web-push.vapid";

    /// <summary>The public key, made now when the tenant has none (audited).</summary>
    public async Task<string> PublicKeyAsync(CancellationToken cancellationToken)
    {
        if (await db.WebPushKeys.AsNoTracking().Select(k => k.PublicKey).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var (publicKey, privateKey) = WebPushCrypto.CreateKeyPair();
        db.WebPushKeys.Add(new WebPushKey { PublicKey = publicKey, PrivateKey = await secrets.ProtectAsync(Purpose, privateKey, cancellationToken).ConfigureAwait(false) });
        audit.Add(AuditCategory.Admin, "notifications.push.key-created", "WebPushKey");
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return publicKey;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Someone else made it at the same moment: theirs counts.
            db.ChangeTracker.Clear();
            return await db.WebPushKeys.AsNoTracking().Select(k => k.PublicKey).FirstAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The key pair for signing, or <see langword="null"/> when the tenant has none yet.</summary>
    public async Task<(string PublicKey, string PrivateKey)?> PairAsync(CancellationToken cancellationToken)
    {
        var key = await db.WebPushKeys.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return key is null ? null : (key.PublicKey, await secrets.UnprotectAsync(Purpose, key.PrivateKey, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Queues web push messages when a notification is rendered (task J2): one per device of the
/// recipient, while the installation offers push, the event's push is on and the person has not
/// turned it off. The text is the event in the recipient's language and the reference number
/// only, never names, titles or descriptions (product owner decision 2026-11-26), because the
/// push services and the lock screen see it.
/// </summary>
internal sealed class PushNotifications(ZelvoDbContext db, NotificationOptions options)
{
    /// <summary>The placeholders that name the thing a notification is about, first found wins.</summary>
    internal static readonly string[] ReferencePlaceholders = [Placeholders.TicketNumber, Placeholders.ArticleNumber, Placeholders.PrivacyRequestNumber];

    public async Task QueueAsync(
        Notification notification, UserAccount recipient, NotificationEventDefinition definition, EventSetting setting,
        IReadOnlyDictionary<string, PlaceholderValue> values, string language, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!options.Push.Enabled || !setting.Enabled || !setting.Push)
        {
            return;
        }

        var devices = await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == recipient.Id).Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (devices.Count == 0
            || await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == recipient.Id && p.EventType == definition.Type)
                .Select(p => (bool?)p.Push).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) == false)
        {
            return;
        }

        var (title, body) = Text(definition.Type, values, language);
        var url = $"/{language}{notification.LinkPath ?? "/notifications"}";
        foreach (var device in devices)
        {
            db.PushDeliveries.Add(new PushDelivery
            {
                TenantId = notification.TenantId,
                NotificationId = notification.Id,
                SubscriptionId = device,
                RecipientId = recipient.Id,
                EventType = definition.Type,
                Language = language,
                Title = title,
                Body = body,
                Url = url.Length <= Notification.LinkPathMaxLength + 3 ? url : $"/{language}/notifications",
                Tag = (notification.TicketId ?? notification.ArticleId ?? notification.Id).ToString(),
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }
    }

    /// <summary>Title and body of a push message: the event and the reference number, in <paramref name="language"/>.</summary>
    internal static (string Title, string Body) Text(string eventType, IReadOnlyDictionary<string, PlaceholderValue> values, string language)
    {
        var terms = NotificationTexts.For(language).Terms;
        var title = terms.GetValueOrDefault($"push.title.{eventType}") ?? terms.GetValueOrDefault("push.default") ?? eventType;
        var reference = ReferencePlaceholders.Select(p => values.GetValueOrDefault(p)?.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
        return (title, reference.Length <= PushDelivery.BodyMaxLength ? reference : reference[..PushDelivery.BodyMaxLength]);
    }
}

/// <summary>What a push service answered.</summary>
public enum PushResultKind
{
    /// <summary>Accepted (201).</summary>
    Sent,

    /// <summary>The subscription no longer exists (404, 410): the device is removed.</summary>
    Gone,

    /// <summary>Refused in a way that will not improve (400, 401, 403, 413).</summary>
    Refused,

    /// <summary>Try again later (429, 5xx, network).</summary>
    Retry,
}

/// <summary>A push service's answer.</summary>
/// <param name="Kind">What it means.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="RetryAfter">How long the service asked to wait.</param>
public sealed record PushResult(PushResultKind Kind, string? Error = null, TimeSpan? RetryAfter = null);

/// <summary>Sends one encrypted message to a push service (RFC 8030), only through the push network policy.</summary>
public interface IWebPushSender
{
    /// <summary>Sends <paramref name="payload"/> to <paramref name="subscription"/>.</summary>
    Task<PushResult> SendAsync(PushSubscription subscription, byte[] payload, string publicKey, string privateKey, string urgency, CancellationToken cancellationToken);
}

/// <summary>
/// The HTTP side of web push: only to the allowed push services, only to addresses the push network
/// policy allows (checked after DNS), no redirects, HTTPS only.
/// </summary>
public sealed class WebPushSender : IWebPushSender, IDisposable
{
    private readonly HttpClient _client;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the sender.</summary>
    public WebPushSender(NotificationOptions options, TimeProvider time)
        : this(options, time, null)
    {
    }

    /// <summary>Tests: sends through <paramref name="handler"/>.</summary>
    internal WebPushSender(NotificationOptions options, TimeProvider time, HttpMessageHandler? handler)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time;
        var policy = new OutboundAddressPolicy(options.Push.Network);
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, token) => policy.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token),
        })
        { Timeout = options.Push.Timeout };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Zelvo/1.0");
    }

    /// <summary>The VAPID contact: the configured one, else the web app's HTTPS address, else the sender address.</summary>
    public string Subject => _options.Push.Subject
        ?? (_options.WebUrl.Scheme == Uri.UriSchemeHttps ? _options.WebUrl.GetLeftPart(UriPartial.Authority) : $"mailto:{_options.Smtp.From}");

    /// <inheritdoc />
    public async Task<PushResult> SendAsync(PushSubscription subscription, byte[] payload, string publicKey, string privateKey, string urgency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!_options.Push.IsAllowedEndpoint(subscription.Endpoint))
        {
            return new PushResult(PushResultKind.Gone, "endpoint-not-allowed");
        }

        var endpoint = new Uri(subscription.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(WebPushCrypto.Encrypt(payload, subscription.P256dh, subscription.Auth)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", ((long)_options.Push.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", urgency);
        request.Headers.TryAddWithoutValidation("Authorization", WebPushCrypto.VapidAuthorization(endpoint, Subject, publicKey, privateKey, _time.GetUtcNow()));
        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return status switch
            {
                >= 200 and < 300 => new PushResult(PushResultKind.Sent),
                404 or 410 => new PushResult(PushResultKind.Gone, $"http-{status}"),
                429 or >= 500 => new PushResult(PushResultKind.Retry, $"http-{status}", response.Headers.RetryAfter?.Delta),
                _ => new PushResult(PushResultKind.Refused, $"http-{status}"),
            };
        }
        catch (OutboundRequestBlockedException blocked)
        {
            return new PushResult(PushResultKind.Refused, blocked.Code);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new PushResult(PushResultKind.Retry, exception is TaskCanceledException ? "timeout" : "network");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}

/// <summary>Sends queued web push messages (worker, task J2) with retries like emails.</summary>
public sealed partial class PushProcessor(
    ZelvoDbContext db, WebPushKeys keys, IWebPushSender sender, NotificationOptions options, TimeProvider time, ILogger<PushProcessor> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Events that ask the push service to wake the device at once (RFC 8030 <c>Urgency: high</c>).</summary>
    private static readonly HashSet<string> Urgent = new(StringComparer.Ordinal)
    {
        NotificationEvents.SlaBreached, NotificationEvents.ApprovalRequested, NotificationEvents.IncidentAssigned, NotificationEvents.IncidentMajorUpdate,
    };

    /// <summary>Sends push message <paramref name="id"/> if it is still due.</summary>
    public async Task SendAsync(long id, CancellationToken cancellationToken)
    {
        var delivery = await db.PushDeliveries.FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        if (delivery is not { Status: PushDeliveryStatus.Pending })
        {
            return;
        }

        var now = time.GetUtcNow();
        delivery.Attempts++;
        delivery.LastAttemptAt = now;
        delivery.LeaseUntil = null;
        var subscription = delivery.SubscriptionId is { } subscriptionId
            ? await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken).ConfigureAwait(false)
            : null;
        var pair = await keys.PairAsync(cancellationToken).ConfigureAwait(false);
        if (subscription is null || pair is null || !options.Push.Enabled)
        {
            Fail(delivery, subscription is null ? "device-removed" : pair is null ? "no-key" : "push-off");
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new PushPayload(delivery.Title, delivery.Body, delivery.Url, delivery.Tag, delivery.Language,
            delivery.Language == "ar" ? "rtl" : "ltr"), Json);
        var result = await sender.SendAsync(subscription, payload, pair.Value.PublicKey, pair.Value.PrivateKey,
            Urgent.Contains(delivery.EventType) ? "high" : "normal", cancellationToken).ConfigureAwait(false);
        switch (result.Kind)
        {
            case PushResultKind.Sent:
                delivery.Status = PushDeliveryStatus.Sent;
                delivery.SentAt = now;
                delivery.NextAttemptAt = null;
                delivery.LastError = null;
                subscription.LastSuccessAt = now;
                break;
            case PushResultKind.Gone:
                // The browser unsubscribed or the push service forgot it: the device goes (audited).
                Fail(delivery, result.Error ?? "gone");
                delivery.SubscriptionId = null;
                db.PushSubscriptions.Remove(subscription);
                break;
            case PushResultKind.Refused:
                Fail(delivery, result.Error ?? "refused");
                break;
            default:
                var delays = options.RetryDelays;
                if (delivery.Attempts > delays.Count)
                {
                    Fail(delivery, result.Error ?? "failed");
                }
                else
                {
                    var wait = delays[delivery.Attempts - 1];
                    delivery.NextAttemptAt = now + (result.RetryAfter is { } after && after > wait && after < TimeSpan.FromHours(12) ? after : wait);
                    delivery.LastError = result.Error;
                    LogRetry(logger, delivery.Id, delivery.Attempts, result.Error);
                }

                break;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Fail(PushDelivery delivery, string error)
    {
        delivery.Status = PushDeliveryStatus.Failed;
        delivery.NextAttemptAt = null;
        delivery.LastError = error.Length <= PushDelivery.ErrorMaxLength ? error : error[..PushDelivery.ErrorMaxLength];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Web push {DeliveryId} was not sent (attempt {Attempt}): {Error}; retrying")]
    private static partial void LogRetry(ILogger logger, long deliveryId, int attempt, string? error);
}

/// <summary>What the service worker receives: the text to show and the page to open.</summary>
/// <param name="Title">Title.</param>
/// <param name="Body">Body (the reference number).</param>
/// <param name="Url">Page in the web app.</param>
/// <param name="Tag">Messages with the same tag replace each other.</param>
/// <param name="Lang">Language.</param>
/// <param name="Dir">Direction of the text.</param>
public sealed record PushPayload(string Title, string Body, string? Url, string? Tag, string Lang, string Dir);

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Notifications;
using Zelvo.Data.Tenancy;

namespace Zelvo.Notifications;

/// <summary>
/// Delivers notifications of the current tenant (scoped; the tenant is set): renders a queued
/// notification for its recipient and queues its email, and sends one email.
/// </summary>
public sealed partial class NotificationProcessor(
    ZelvoDbContext db, NotificationComposer composer, NotificationOptions options, IEmailSender sender, TimeProvider time,
    ILogger<NotificationProcessor> logger)
{
    /// <summary>Rendering attempts before a notification is given up.</summary>
    public const int MaxRenderAttempts = 5;

    /// <summary>Renders notification <paramref name="id"/> if it is still pending.</summary>
    public async Task RenderAsync(Guid id, CancellationToken cancellationToken)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id, cancellationToken).ConfigureAwait(false);
        if (notification is not { Status: NotificationStatus.Pending })
        {
            return;
        }

        try
        {
            await RenderCoreAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var attempts = notification.Attempts + 1;
            LogRenderFailed(logger, id, attempts, exception);
            await db.Notifications.Where(n => n.Id == id).ExecuteUpdateAsync(set => set
                .SetProperty(n => n.Attempts, attempts)
                .SetProperty(n => n.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(n => n.Status, attempts >= MaxRenderAttempts ? NotificationStatus.Suppressed : NotificationStatus.Pending)
                .SetProperty(n => n.ProcessedAt, attempts >= MaxRenderAttempts ? time.GetUtcNow() : null), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RenderCoreAsync(Notification notification, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var definition = NotificationEvents.Find(notification.EventType);
        var recipient = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == notification.RecipientId, cancellationToken).ConfigureAwait(false);
        var setting = NotificationSettings.For(await NotificationSettings.ReadAsync(db, cancellationToken).ConfigureAwait(false), notification.EventType);
        notification.ProcessedAt = now;
        notification.LeaseUntil = null;
        if (definition is null || recipient is not { Status: UserStatus.Active } || !setting.Enabled)
        {
            notification.Status = NotificationStatus.Suppressed;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var values = NotificationData.Deserialize(notification.Data);
        var format = await composer.FormatAsync(recipient, cancellationToken).ConfigureAwait(false);
        var template = await composer.TemplateAsync(notification.EventType, format.Language, cancellationToken).ConfigureAwait(false);
        var rendered = await composer.RenderAsync(template, values, recipient, format, notification.LinkPath, cancellationToken).ConfigureAwait(false);
        notification.Status = NotificationStatus.Delivered;
        notification.Language = format.Language;
        notification.Title = rendered.Subject;
        notification.Text = rendered.InApp;

        if (setting.Email && recipient.Email is { Length: > 0 } address && await WantsEmailAsync(recipient, definition, cancellationToken).ConfigureAwait(false))
        {
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                TenantId = notification.TenantId,
                NotificationId = notification.Id,
                RecipientId = recipient.Id,
                EventType = notification.EventType,
                Address = address,
                Language = format.Language,
                Subject = rendered.Subject,
                HtmlBody = rendered.Html,
                TextBody = rendered.Text,
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }

        // B3 recorded the alert; it counts as delivered once its first notification is out.
        if (notification.SlaAlertId is { } alertId
            && await db.SlaAlerts.FirstOrDefaultAsync(a => a.Id == alertId && a.DeliveredAt == null, cancellationToken).ConfigureAwait(false) is { } alert)
        {
            alert.DeliveredAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WantsEmailAsync(UserAccount recipient, NotificationEventDefinition definition, CancellationToken cancellationToken) =>
        !definition.EmailOptional
        || await db.NotificationPreferences.AsNoTracking()
            .Where(p => p.UserId == recipient.Id && p.EventType == definition.Type)
            .Select(p => (bool?)p.Email)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) != false;

    /// <summary>Sends email <paramref name="id"/> once if it is still pending; schedules the next attempt or gives up.</summary>
    public async Task<EmailResult?> SendAsync(long id, CancellationToken cancellationToken)
    {
        var delivery = await db.NotificationDeliveries.FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        if (delivery is not { Status: NotificationDeliveryStatus.Pending })
        {
            return null;
        }

        var letterhead = await composer.LetterheadAsync(cancellationToken).ConfigureAwait(false);
        var domain = options.Smtp.From.Split('@') is [_, { Length: > 0 } host] ? host : "zelvo.invalid";
        var message = new EmailMessage(delivery.Address, letterhead.NameIn(delivery.Language), delivery.Subject, delivery.HtmlBody, delivery.TextBody,
            delivery.Language, $"zelvo.{delivery.TenantId:N}.{delivery.Id}@{domain}");
        var result = await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);

        var now = time.GetUtcNow();
        delivery.Attempts++;
        delivery.LastAttemptAt = now;
        delivery.LeaseUntil = null;
        if (result.Sent)
        {
            delivery.Status = NotificationDeliveryStatus.Sent;
            delivery.SentAt = now;
            delivery.NextAttemptAt = null;
            delivery.LastError = null;
        }
        else
        {
            delivery.LastError = result.Error;
            if (result.Permanent || delivery.Attempts > options.RetryDelays.Count)
            {
                delivery.Status = NotificationDeliveryStatus.Failed;
                delivery.NextAttemptAt = null;
            }
            else
            {
                delivery.NextAttemptAt = now + options.RetryDelays[delivery.Attempts - 1];
            }

            LogSendFailed(logger, id, delivery.Attempts, result.Error);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} could not be rendered (attempt {Attempt}).")]
    private static partial void LogRenderFailed(ILogger logger, Guid notificationId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email {DeliveryId} was not sent (attempt {Attempt}): {Error}")]
    private static partial void LogSendFailed(ILogger logger, long deliveryId, int attempt, string? error);
}

/// <summary>
/// Worker job: renders pending notifications and sends due emails, tenant by tenant. Work is
/// claimed with a short lease (<c>FOR UPDATE SKIP LOCKED</c>), so several workers share it and work
/// of a stopped worker is picked up when the lease ends. Each item runs in its own scope with the
/// tenant set, so row-level security applies.
/// </summary>
public sealed partial class NotificationDispatcher(IServiceScopeFactory scopes, NotificationOptions options, ILogger<NotificationDispatcher> logger)
    : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>Does everything due now; returns how many notifications and emails were handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var handled = 0;
        foreach (var (tenantId, slug) in await DueTenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            handled += await RunTenantAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
        }

        return handled;
    }

    /// <summary>Does everything due now for one tenant.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        var handled = 0;
        List<Guid> notifications;
        do
        {
            notifications = await ClaimNotificationsAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
            foreach (var id in notifications)
            {
                await InScopeAsync(tenantId, slug, p => p.RenderAsync(id, cancellationToken)).ConfigureAwait(false);
                handled++;
            }
        }
        while (notifications.Count == BatchSize);

        List<long> deliveries;
        do
        {
            deliveries = await ClaimDeliveriesAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
            await Parallel.ForEachAsync(deliveries,
                new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = cancellationToken },
                async (id, token) =>
                {
                    await InScopeAsync(tenantId, slug, p => p.SendAsync(id, token)).ConfigureAwait(false);
                    Interlocked.Increment(ref handled);
                }).ConfigureAwait(false);
        }
        while (deliveries.Count == BatchSize);

        return handled;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRunFailed(logger, exception);
            }

            try
            {
                await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task InScopeAsync(Guid tenantId, string slug, Func<NotificationProcessor, Task> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "notifications", "Notifications");
        try
        {
            await work(scope.ServiceProvider.GetRequiredService<NotificationProcessor>()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The lease ends and the item is tried again.
            LogItemFailed(logger, slug, exception);
        }
    }

    private async Task<List<(Guid Id, string Slug)>> DueTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_notification_due_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    private async Task<List<Guid>> ClaimNotificationsAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var leaseUntil = DateTimeOffset.UtcNow + Lease;
        return await db.Database.SqlQuery<Guid>($"""
            UPDATE notifications SET lease_until = {leaseUntil}
            WHERE id IN (
                SELECT id FROM notifications
                WHERE status = 'Pending' AND (lease_until IS NULL OR lease_until < now())
                ORDER BY created_at, id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<long>> ClaimDeliveriesAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var leaseUntil = DateTimeOffset.UtcNow + Lease;
        return await db.Database.SqlQuery<long>($"""
            UPDATE notification_deliveries SET lease_until = {leaseUntil}
            WHERE id IN (
                SELECT id FROM notification_deliveries
                WHERE status = 'Pending' AND next_attempt_at <= now() AND (lease_until IS NULL OR lease_until < now())
                ORDER BY next_attempt_at, id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivering notifications failed; retrying")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notification of tenant {Tenant} could not be handled; it will be retried")]
    private static partial void LogItemFailed(ILogger logger, string tenant, Exception exception);
}

/// <summary>Worker job: removes finished emails and in-app notifications past their retention, every hour.</summary>
public sealed partial class NotificationPurge(IServiceScopeFactory scopes, NotificationOptions options, ILogger<NotificationPurge> logger) : BackgroundService
{
    /// <summary>Removes old records now; returns how many.</summary>
    public async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var deliveries = now - options.DeliveryRetention;
        var notifications = now - options.InAppRetention;
        var removed = await db.Database.SqlQuery<long>($"SELECT zelvo_purge_notifications({deliveries}, {notifications}) AS \"Value\"")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            LogPurged(logger, removed);
        }

        return removed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPurgeFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} notifications and emails past their retention.")]
    private static partial void LogPurged(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing old notifications failed; trying again in an hour")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}

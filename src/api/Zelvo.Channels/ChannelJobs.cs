using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;
using Zelvo.Secrets;
using Zelvo.Tickets;

namespace Zelvo.Channels;

/// <summary>Registers email and chat for the API and the worker.</summary>
public static class ChannelsServiceCollectionExtensions
{
    /// <summary>What both hosts need: options, the HTTP client and tokens for Microsoft and Google, the mailbox clients and the intake.</summary>
    public static IServiceCollection AddZelvoChannels(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ => ChannelOptions.Read(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ChannelHttp(sp.GetRequiredService<ChannelOptions>(), new OutboundAddressPolicy(sp.GetRequiredService<ChannelOptions>().Network)));
        services.TryAddSingleton<OAuthTokens>();
        services.TryAddSingleton<IMailboxClients, MailboxClients>();
        services.TryAddScoped<EmailIntake>();
        services.TryAddScoped<MailboxReader>();
        services.TryAddScoped<OutboundEmailSender>();
        return services;
    }

    /// <summary>The worker side: reading mailboxes, sending replies, ending idle chats and removing old logs.</summary>
    public static IServiceCollection AddZelvoChannelJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoChannels(configuration);
        services.TryAddSingleton<MailJob>();
        services.TryAddSingleton<ChatIdleJob>();
        services.TryAddSingleton<MailLogPurge>();
        services.AddHostedService(sp => sp.GetRequiredService<MailJob>());
        services.AddHostedService(sp => sp.GetRequiredService<ChatIdleJob>());
        services.AddHostedService(sp => sp.GetRequiredService<MailLogPurge>());
        return services;
    }
}

/// <summary>What one read of a mailbox did.</summary>
/// <param name="Read">Emails read.</param>
/// <param name="Error">Why the read failed, or null.</param>
public sealed record MailboxReadResult(int Read, string? Error);

/// <summary>
/// Reads one mailbox of the current tenant (scoped): its unread emails, each processed by
/// <see cref="EmailIntake"/> in a scope of its own and then marked read. Status fields are written
/// without the change tracker, so polling does not fill the audit log.
/// </summary>
public sealed partial class MailboxReader(
    ZelvoDbContext db, IServiceScopeFactory scopes, ITenantContext tenant, IMailboxClients clients, ISecretProtector secrets,
    ChannelOptions options, TimeProvider time, ILogger<MailboxReader> logger)
{
    /// <summary>An email that failed this many times is marked read and left in the log as failed.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Reads mailbox <paramref name="mailboxId"/> now.</summary>
    public async Task<MailboxReadResult> ReadAsync(Guid mailboxId, CancellationToken cancellationToken)
    {
        var mailbox = await db.Mailboxes.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mailboxId, cancellationToken).ConfigureAwait(false);
        if (mailbox is null)
        {
            return new(0, null);
        }

        var read = 0;
        string? error = null;
        try
        {
            var credential = await MailboxCredential.ReadAsync(mailbox, secrets, cancellationToken).ConfigureAwait(false);
            await using var connection = await clients.OpenAsync(mailbox, credential, cancellationToken).ConfigureAwait(false);
            foreach (var id in await connection.ListUnreadAsync(options.MaxEmailsPerRead, cancellationToken).ConfigureAwait(false))
            {
                var message = await connection.GetAsync(id, options.MaxEmailBytes, cancellationToken).ConfigureAwait(false);
                if (await ProcessAsync(mailbox, id, message, cancellationToken).ConfigureAwait(false))
                {
                    await connection.MarkReadAsync(id, cancellationToken).ConfigureAwait(false);
                }

                read++;
            }

            await StoreRotatedTokenAsync(mailbox, credential, connection.RotatedRefreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (MailboxException ex)
        {
            error = Cut(ex.Summary, Mailbox.ErrorMaxLength);
            LogReadFailed(logger, mailbox.Id, ex.Code);
        }

        var now = time.GetUtcNow();
        var failures = error is null ? 0 : mailbox.ConsecutiveFailures + 1;
        // Failing mailboxes are read less often, up to every 30 minutes.
        var wait = error is null ? options.PollInterval
            : TimeSpan.FromTicks(Math.Min(options.PollInterval.Ticks * (1L << Math.Min(failures, 6)), TimeSpan.FromMinutes(30).Ticks));
        await db.Mailboxes.Where(m => m.Id == mailbox.Id).ExecuteUpdateAsync(set => set
            .SetProperty(m => m.LastCheckedAt, now)
            .SetProperty(m => m.LastCheckSucceeded, error == null)
            .SetProperty(m => m.LastError, error)
            .SetProperty(m => m.ConsecutiveFailures, failures)
            .SetProperty(m => m.NextCheckAt, now + wait)
            .SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
        return new(read, error);
    }

    /// <summary>Processes one email in its own scope; whether it can be marked read.</summary>
    private async Task<bool> ProcessAsync(Mailbox mailbox, string id, MimeMessage? message, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(mailbox.TenantId, tenant.Slug ?? string.Empty);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "channels.email", $"Email: {mailbox.Name}");
        var intake = scope.ServiceProvider.GetRequiredService<EmailIntake>();
        try
        {
            await intake.ProcessAsync(mailbox, id, message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MailboxException)
        {
            LogEmailFailed(logger, mailbox.Id, ex);
            await intake.RecordFailureAsync(mailbox, id, ex is DbUpdateException ? "database-error" : "processing-error", cancellationToken).ConfigureAwait(false);
            var attempts = await db.InboundEmails.AsNoTracking().Where(e => e.MailboxId == mailbox.Id && e.ProviderMessageId == id)
                .Select(e => e.Attempts).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return attempts >= MaxAttempts;
        }
    }

    private async Task StoreRotatedTokenAsync(Mailbox mailbox, MailboxCredential credential, string? rotated, CancellationToken cancellationToken)
    {
        if (rotated is null)
        {
            return;
        }

        var tracked = await db.Mailboxes.SingleAsync(m => m.Id == mailbox.Id, cancellationToken).ConfigureAwait(false);
        tracked.CredentialCiphertext = await MailboxCredential.ProtectAsync(tracked, credential with { RefreshToken = rotated }, secrets, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading mailbox {MailboxId} failed: {Code}")]
    private static partial void LogReadFailed(ILogger logger, Guid mailboxId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An email of mailbox {MailboxId} could not be processed; it is tried again")]
    private static partial void LogEmailFailed(ILogger logger, Guid mailboxId, Exception exception);
}

/// <summary>
/// Sends one queued email from its ticket's mailbox (scoped; the tenant is set), with the thread's
/// headers so the requester's mail app keeps the conversation together.
/// </summary>
public sealed partial class OutboundEmailSender(
    ZelvoDbContext db, IMailboxClients clients, ISecretProtector secrets, ChannelOptions options, TimeProvider time, ILogger<OutboundEmailSender> logger)
{
    /// <summary>Sends email <paramref name="id"/> if it is still pending; schedules the next attempt or gives up.</summary>
    public async Task SendAsync(long id, CancellationToken cancellationToken)
    {
        var email = await db.OutboundEmails.SingleOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
        if (email is not { Status: OutboundEmailStatus.Pending })
        {
            return;
        }

        var mailbox = await db.Mailboxes.AsNoTracking().SingleOrDefaultAsync(m => m.Id == email.MailboxId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (mailbox is not { Status: MailboxStatus.Active })
        {
            // Waits until an administrator turns the mailbox on again.
            email.NextAttemptAt = now + TimeSpan.FromMinutes(10);
            email.LeaseUntil = null;
            email.LastError = "mailbox-disabled: The mailbox is turned off; the email waits.";
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        string? error = null;
        var permanent = false;
        try
        {
            var credential = await MailboxCredential.ReadAsync(mailbox, secrets, cancellationToken).ConfigureAwait(false);
            using var message = await BuildAsync(email, mailbox, cancellationToken).ConfigureAwait(false);
            var rotated = await clients.SendAsync(mailbox, credential, message, cancellationToken).ConfigureAwait(false);
            if (rotated is not null)
            {
                var tracked = await db.Mailboxes.SingleAsync(m => m.Id == mailbox.Id, cancellationToken).ConfigureAwait(false);
                tracked.CredentialCiphertext = await MailboxCredential.ProtectAsync(tracked, credential with { RefreshToken = rotated }, secrets, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (MailboxException ex)
        {
            error = ex.Summary.Length <= Mailbox.ErrorMaxLength ? ex.Summary : ex.Summary[..Mailbox.ErrorMaxLength];
            permanent = ex.Code is "smtp-refused" or "credential-invalid";
            LogSendFailed(logger, id, ex.Code);
        }
        catch (ParseException)
        {
            error = "address-invalid: The requester's address is not valid.";
            permanent = true;
        }

        email.Attempts++;
        email.LeaseUntil = null;
        email.LastError = error;
        MessageDelivery? delivery = null;
        if (error is null)
        {
            email.Status = OutboundEmailStatus.Sent;
            email.SentAt = now;
            email.NextAttemptAt = null;
            delivery = MessageDelivery.Sent;
        }
        else if (permanent || email.Attempts > options.RetryDelays.Count)
        {
            email.Status = OutboundEmailStatus.Failed;
            email.NextAttemptAt = null;
            delivery = MessageDelivery.Failed;
        }
        else
        {
            email.NextAttemptAt = now + options.RetryDelays[email.Attempts - 1];
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (delivery is { } state && email.TicketMessageId is { } messageId)
        {
            // Delivery state only; the message itself does not change (not audited).
            await db.TicketMessages.Where(m => m.Id == messageId).ExecuteUpdateAsync(set => set.SetProperty(m => m.Delivery, state), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The MIME message of <paramref name="email"/>: from the mailbox in the tenant's name, threaded, HTML and text.</summary>
    internal async Task<MimeMessage> BuildAsync(OutboundEmail email, Mailbox mailbox, CancellationToken cancellationToken)
    {
        var name = await db.Tenants.AsNoTracking().Select(t => t.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(name?.GetOrFallback(email.Language) ?? mailbox.Name, mailbox.Address));
        message.To.Add(MailboxAddress.Parse(email.ToAddress));
        message.Subject = email.Subject;
        message.MessageId = email.MessageId;
        if (email.InReplyTo is { } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
        }

        foreach (var reference in (email.References ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            message.References.Add(reference);
        }

        // Notifications are automatic (RFC 3834); staff replies are written by a person.
        if (email.TicketMessageId is null)
        {
            message.Headers.Add("Auto-Submitted", "auto-generated");
        }

        message.Headers.Add("X-Auto-Response-Suppress", "All");
        message.Headers.Add(HeaderId.ContentLanguage, email.Language);
        message.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();
        return message;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending email {EmailId} failed: {Code}")]
    private static partial void LogSendFailed(ILogger logger, long emailId, string code);
}

/// <summary>
/// Worker job: reads mailboxes that are due and sends queued emails, tenant by tenant. Mailboxes and
/// emails are claimed with a lease (<c>FOR UPDATE SKIP LOCKED</c>), so several workers share the work.
/// </summary>
public sealed partial class MailJob(IServiceScopeFactory scopes, ChannelOptions options, ILogger<MailJob> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan MailboxLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EmailLease = TimeSpan.FromMinutes(2);

    /// <summary>Does everything due now; returns how many mailboxes and emails were handled.</summary>
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
        foreach (var mailbox in await ClaimAsync<Guid>(tenantId, slug, $"""
            UPDATE mailboxes SET lease_until = {DateTimeOffset.UtcNow + MailboxLease}
            WHERE id IN (
                SELECT id FROM mailboxes
                WHERE status = 'Active' AND next_check_at <= now() AND (lease_until IS NULL OR lease_until < now())
                ORDER BY next_check_at LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """, cancellationToken).ConfigureAwait(false))
        {
            await InScopeAsync(tenantId, slug, sp => sp.GetRequiredService<MailboxReader>().ReadAsync(mailbox, cancellationToken)).ConfigureAwait(false);
            handled++;
        }

        List<long> emails;
        do
        {
            emails = await ClaimAsync<long>(tenantId, slug, $"""
                UPDATE outbound_emails SET lease_until = {DateTimeOffset.UtcNow + EmailLease}
                WHERE id IN (
                    SELECT id FROM outbound_emails
                    WHERE status = 'Pending' AND next_attempt_at <= now() AND (lease_until IS NULL OR lease_until < now())
                    ORDER BY next_attempt_at, id LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """, cancellationToken).ConfigureAwait(false);
            foreach (var id in emails)
            {
                await InScopeAsync(tenantId, slug, sp => sp.GetRequiredService<OutboundEmailSender>().SendAsync(id, cancellationToken)).ConfigureAwait(false);
                handled++;
            }
        }
        while (emails.Count == BatchSize);

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
                await Task.Delay(options.WorkInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task InScopeAsync(Guid tenantId, string slug, Func<IServiceProvider, Task> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "channels.email", "Email");
        try
        {
            await work(scope.ServiceProvider).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The lease ends and the work is tried again.
            LogItemFailed(logger, slug, exception);
        }
    }

    private async Task<List<T>> ClaimAsync<T>(Guid tenantId, string slug, FormattableString sql, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        return await db.Database.SqlQuery<T>(sql).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<(Guid Id, string Slug)>> DueTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_mail_due_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading mailboxes and sending replies failed; retrying")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mail work of tenant {Tenant} failed; it will be retried")]
    private static partial void LogItemFailed(ILogger logger, string tenant, Exception exception);
}

/// <summary>
/// Worker job: ends live chats whose visitor has not written or looked for
/// <see cref="ChannelOptions.ChatIdleTimeout"/>, with a system message, every minute.
/// </summary>
public sealed partial class ChatIdleJob(IServiceScopeFactory scopes, ChannelOptions options, TimeProvider time, ILogger<ChatIdleJob> logger) : BackgroundService
{
    /// <summary>Ends idle chats now; returns how many.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow() - options.ChatIdleTimeout;
        var tenants = new List<(Guid Id, string Slug)>();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_idle_chat_tenants(@before)", (NpgsqlConnection)db.Database.GetDbConnection());
            command.Parameters.AddWithValue("before", before);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tenants.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        var ended = 0;
        foreach (var (tenantId, slug) in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
            scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "channels.chat", "Live chat");
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var now = time.GetUtcNow();
            var sessions = await db.ChatSessions.Where(c => c.State != ChatState.Ended && c.LastVisitorActivityAt < before).Take(200).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var session in sessions)
            {
                session.State = ChatState.Ended;
                session.EndedAt = now;
                session.EndedBy = ChatEndedBy.System;
                db.ChatMessages.Add(new ChatMessage { TenantId = tenantId, SessionId = session.Id, Sender = ChatSender.System, Body = string.Empty, Event = ChatEvents.Ended, CreatedAt = now });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                ended += sessions.Count;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A chat changed meanwhile; the next run looks again.
            }
        }

        return ended;
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
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ending idle chats failed; retrying")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Events of system messages in a chat.</summary>
public static class ChatEvents
{
    /// <summary>An agent accepted the chat.</summary>
    public const string Accepted = "accepted";

    /// <summary>The chat moved to another agent or group.</summary>
    public const string Transferred = "transferred";

    /// <summary>The chat ended.</summary>
    public const string Ended = "ended";

    /// <summary>A ticket was made from the chat, or the chat was linked to one.</summary>
    public const string Ticket = "ticket";
}

/// <summary>Worker job: removes old mailbox log entries and finished outgoing emails, every hour.</summary>
public sealed partial class MailLogPurge(IServiceScopeFactory scopes, ChannelOptions options, ILogger<MailLogPurge> logger) : BackgroundService
{
    /// <summary>Removes old records now; returns how many.</summary>
    public async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT zelvo_purge_mail_logs(@inbound, @outbound)", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("inbound", now - options.InboundLogRetention);
        command.Parameters.AddWithValue("outbound", now - options.OutboundRetention);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
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
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing old mail logs failed; retrying")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

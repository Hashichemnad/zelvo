using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Tenancy;
using Zelvo.Secrets;

namespace Zelvo.Channels.Messaging;

/// <summary>A channel's secrets, encrypted with the tenant's data key under purposes of their own.</summary>
public static class MessagingSecretStore
{
    /// <summary>Purpose of a channel's credential.</summary>
    public static string CredentialPurpose(Guid channelId) => $"messaging.channel:{channelId:N}.credential";

    /// <summary>Purpose of a channel's webhook secrets.</summary>
    public static string WebhookPurpose(Guid channelId) => $"messaging.channel:{channelId:N}.webhook";

    /// <summary>Decrypts <paramref name="channel"/>'s secrets.</summary>
    public static async Task<MessagingSecrets> ReadAsync(ISecretProtector secrets, MessagingChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(channel);
        var credential = channel.CredentialCiphertext is { } c ? await secrets.UnprotectAsync(CredentialPurpose(channel.Id), c, cancellationToken).ConfigureAwait(false) : null;
        WebhookSecrets? webhook = null;
        if (channel.WebhookSecretCiphertext is { } w)
        {
            webhook = JsonSerializer.Deserialize<WebhookSecrets>(await secrets.UnprotectAsync(WebhookPurpose(channel.Id), w, cancellationToken).ConfigureAwait(false), Json);
        }

        return new MessagingSecrets(credential, webhook);
    }

    /// <summary>Encrypts and stores the webhook secrets.</summary>
    public static async Task WriteWebhookAsync(ISecretProtector secrets, MessagingChannel channel, WebhookSecrets value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(channel);
        channel.WebhookSecretCiphertext = await secrets.ProtectAsync(WebhookPurpose(channel.Id), JsonSerializer.Serialize(value, Json), cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Sends one queued channel message (scoped; the tenant is set): WhatsApp text or template, SMS or a
/// Teams message or card. Retries after the channels' delays (1 min … 12 h); refusals that will not
/// improve (a refused number, outside WhatsApp's window without a template) fail at once.
/// </summary>
public sealed partial class MessagingSender(ZelvoDbContext db, ISecretProtector secrets, IMessagingClients clients, ChannelOptions options, TimeProvider time, ILogger<MessagingSender> logger)
{
    /// <summary>Sends message <paramref name="id"/> if it still waits.</summary>
    public async Task SendAsync(long id, CancellationToken cancellationToken)
    {
        var message = await db.ChannelMessages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken).ConfigureAwait(false);
        if (message is not { Status: ChannelMessageStatus.Pending, Direction: MessageDirection.Out })
        {
            return;
        }

        var channel = await db.MessagingChannels.FirstOrDefaultAsync(c => c.Id == message.ChannelId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        message.LeaseUntil = null;
        if (channel is not { Active: true })
        {
            // A channel that is off keeps its messages waiting, as a turned-off mailbox does.
            message.NextAttemptAt = now + options.RetryDelays[^1];
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var contact = message.ContactId is { } contactId
            ? await db.MessagingContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken).ConfigureAwait(false)
            : null;
        message.Attempts++;
        try
        {
            var keys = await MessagingSecretStore.ReadAsync(secrets, channel, cancellationToken).ConfigureAwait(false);
            message.ProviderMessageId = await clients.SendAsync(channel, keys, new OutgoingMessage(message.Kind, message.Address ?? string.Empty, message.Text, message.Payload, contact), cancellationToken)
                .ConfigureAwait(false);
            message.Status = ChannelMessageStatus.Sent;
            message.SentAt = now;
            message.Reason = null;
        }
        catch (Exception ex) when (ex is MessagingException or SecretDecryptionException)
        {
            var failure = ex as MessagingException ?? new MessagingException("key-unreadable", "The stored credential cannot be decrypted. Enter it again.", true);
            LogSendFailed(logger, id, message.Attempts, failure.Code);
            var giveUp = failure.Permanent || message.Attempts > options.RetryDelays.Count;
            message.Status = giveUp ? ChannelMessageStatus.Failed : ChannelMessageStatus.Pending;
            message.NextAttemptAt = giveUp ? null : now + options.RetryDelays[message.Attempts - 1];
            message.Reason = failure.Code.Length <= ChannelMessage.ReasonMaxLength ? failure.Code : failure.Code[..ChannelMessage.ReasonMaxLength];
            channel.LastError = failure.Summary.Length <= MessagingChannel.ErrorMaxLength ? failure.Summary : failure.Summary[..MessagingChannel.ErrorMaxLength];
            channel.LastErrorAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Channel message {MessageId} was not sent (attempt {Attempt}): {Code}")]
    private static partial void LogSendFailed(ILogger logger, long messageId, int attempt, string code);
}

/// <summary>
/// Worker job (task E3): sends queued WhatsApp, SMS and Teams messages tenant by tenant, each with
/// a two-minute lease (<c>FOR UPDATE SKIP LOCKED</c>) so several workers share the queue, and
/// removes the channel log after <see cref="ChannelOptions.MessageRetention"/>.
/// </summary>
public sealed partial class MessagingJob(IServiceScopeFactory scopes, ChannelOptions options, ILogger<MessagingJob> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private DateTimeOffset _nextPurge = DateTimeOffset.MinValue;

    /// <summary>One pass over every tenant with messages due.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var handled = 0;
        foreach (var (tenantId, slug) in await DueTenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            handled += await RunTenantAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
        }

        if (DateTimeOffset.UtcNow >= _nextPurge)
        {
            _nextPurge = DateTimeOffset.UtcNow.AddHours(1);
            await PurgeAsync(cancellationToken).ConfigureAwait(false);
        }

        return handled;
    }

    /// <summary>Sends the due messages of one tenant.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        var handled = 0;
        List<long> due;
        do
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
                var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
                due = await db.Database.SqlQuery<long>($"""
                    UPDATE channel_messages SET lease_until = {DateTimeOffset.UtcNow + Lease}
                    WHERE id IN (
                        SELECT id FROM channel_messages
                        WHERE status = 'Pending' AND next_attempt_at <= now() AND (lease_until IS NULL OR lease_until < now())
                        ORDER BY next_attempt_at, id LIMIT {BatchSize}
                        FOR UPDATE SKIP LOCKED)
                    RETURNING id AS "Value"
                    """).ToListAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var id in due)
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
                scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "channels.messaging", "Messaging");
                try
                {
                    await scope.ServiceProvider.GetRequiredService<MessagingSender>().SendAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The lease ends and the message is tried again.
                    LogItemFailed(logger, slug, exception);
                }

                handled++;
            }
        }
        while (due.Count == BatchSize);

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
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT zelvo_purge_channel_messages(@before)", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("before", DateTimeOffset.UtcNow - options.MessageRetention);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<(Guid Id, string Slug)>> DueTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_messaging_due_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending channel messages failed; trying again shortly.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A channel message of tenant {Tenant} failed; it is tried again.")]
    private static partial void LogItemFailed(ILogger logger, string tenant, Exception exception);
}

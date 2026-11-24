using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Zelvo.Api.Channels;

/// <summary>
/// Live chat signals over SignalR (task E2). The hub only tells clients that something changed;
/// they then reload through the REST API, which checks access on every call. Clients join the hub's
/// groups with a short-lived watch key from <c>POST /chat/watch</c>, so the hub itself never reads
/// the database.
/// </summary>
internal sealed class ChatHub(ChatSignals signals) : Hub
{
    /// <summary>Joins the groups a watch key allows; returns how many.</summary>
    public async Task<int> Watch(string key)
    {
        var groups = await signals.RedeemAsync(key).ConfigureAwait(false);
        foreach (var group in groups)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group).ConfigureAwait(false);
        }

        return groups.Count;
    }
}

/// <summary>
/// Hub group names, watch keys and fan-out. Watch keys live two minutes in Valkey (in memory when
/// Valkey is away); every signal is sent to this instance's clients and published on Valkey for
/// the other API instances, which pass it on to theirs.
/// </summary>
internal sealed partial class ChatSignals(
    IHubContext<ChatHub> hub, Lazy<Task<IConnectionMultiplexer>> valkey, ILogger<ChatSignals> logger) : IHostedService
{
    /// <summary>Event clients get when a chat changed (state, messages).</summary>
    public const string ChatChanged = "chatChanged";

    /// <summary>Event agents get when a queue or their own chats changed.</summary>
    public const string QueueChanged = "queueChanged";

    private const string Channel = "zelvo:chat:signals";
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromMinutes(2);
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, (string[] Groups, DateTimeOffset Expires)> _local = new(StringComparer.Ordinal);

    public static string ChatGroup(Guid tenantId, Guid sessionId) => $"t:{tenantId:N}:chat:{sessionId:N}";

    public static string QueueGroup(Guid tenantId, Guid groupId) => $"t:{tenantId:N}:queue:{groupId:N}";

    public static string AgentGroup(Guid tenantId, Guid userId) => $"t:{tenantId:N}:agent:{userId:N}";

    /// <summary>A watch key for <paramref name="groups"/> (checked by the caller).</summary>
    public async Task<string> IssueAsync(IReadOnlyCollection<string> groups)
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        if (await DatabaseAsync().ConfigureAwait(false) is { } db)
        {
            await db.StringSetAsync($"zelvo:chat:watch:{key}", JsonSerializer.Serialize(groups), KeyLifetime).ConfigureAwait(false);
        }
        else
        {
            foreach (var expired in _local.Where(e => e.Value.Expires < DateTimeOffset.UtcNow).Select(e => e.Key).ToList())
            {
                _local.TryRemove(expired, out _);
            }

            _local[key] = ([.. groups], DateTimeOffset.UtcNow + KeyLifetime);
        }

        return key;
    }

    /// <summary>The groups of a watch key; each key works once.</summary>
    public async Task<IReadOnlyList<string>> RedeemAsync(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 64)
        {
            return [];
        }

        if (_local.TryRemove(key, out var local))
        {
            return local.Expires >= DateTimeOffset.UtcNow ? local.Groups : [];
        }

        if (await DatabaseAsync().ConfigureAwait(false) is not { } db)
        {
            return [];
        }

        var value = await db.StringGetDeleteAsync($"zelvo:chat:watch:{key}").ConfigureAwait(false);
        return value.HasValue ? JsonSerializer.Deserialize<string[]>(value.ToString()) ?? [] : [];
    }

    /// <summary>Tells the clients of <paramref name="groups"/> about <paramref name="sessionId"/>.</summary>
    public async Task SendAsync(string eventName, Guid sessionId, IEnumerable<string> groups)
    {
        var list = groups.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            return;
        }

        try
        {
            await hub.Clients.Groups(list).SendAsync(eventName, sessionId).ConfigureAwait(false);
            if (await ConnectionAsync().ConfigureAwait(false) is { } connection)
            {
                await connection.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel),
                    JsonSerializer.Serialize(new Signal(_instance, eventName, sessionId, [.. list]))).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or InvalidOperationException)
        {
            // Clients also poll; a lost signal only delays them.
            LogSignalFailed(logger, ex);
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe in the background: the API starts even when Valkey is away.
        _ = Task.Run(async () =>
        {
            try
            {
                if (await ConnectionAsync().ConfigureAwait(false) is { } connection)
                {
                    await connection.GetSubscriber().SubscribeAsync(RedisChannel.Literal(Channel), (_, message) => RelayInBackground(message)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogSignalFailed(logger, ex);
            }
        }, CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RelayInBackground(RedisValue message) => _ = RelayAsync(message);

    private async Task RelayAsync(RedisValue message)
    {
        try
        {
            var signal = JsonSerializer.Deserialize<Signal>(message.ToString());
            if (signal is null || signal.Instance == _instance || signal.Groups.Length == 0)
            {
                return;
            }

            await hub.Clients.Groups(signal.Groups).SendAsync(signal.Event, signal.SessionId).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            LogSignalFailed(logger, ex);
        }
    }

    private async Task<IConnectionMultiplexer?> ConnectionAsync()
    {
        try
        {
            var connection = await valkey.Value.ConfigureAwait(false);
            return connection.IsConnected ? connection : null;
        }
        catch (RedisException)
        {
            return null;
        }
    }

    private async Task<IDatabase?> DatabaseAsync() => (await ConnectionAsync().ConfigureAwait(false))?.GetDatabase();

    private sealed record Signal(string Instance, string Event, Guid SessionId, string[] Groups);

    [LoggerMessage(Level = LogLevel.Information, Message = "A live chat signal could not be passed on; clients catch up by polling")]
    private static partial void LogSignalFailed(ILogger logger, Exception exception);
}

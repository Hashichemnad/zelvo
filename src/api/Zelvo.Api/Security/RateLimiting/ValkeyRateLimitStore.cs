using StackExchange.Redis;

namespace Zelvo.Api.Security.RateLimiting;

/// <summary>
/// Fixed-window counters in Valkey (Redis protocol), shared by every API instance. One atomic
/// script per request: increment, and start the window's expiry on the first request.
/// </summary>
internal sealed partial class ValkeyRateLimitStore(Lazy<Task<IConnectionMultiplexer>> connection, ILogger<ValkeyRateLimitStore> logger) : IRateLimitStore
{
    private static readonly LuaScript Hit = LuaScript.Prepare("""
        local count = redis.call('INCR', @key)
        local ttl = redis.call('PTTL', @key)
        if ttl < 0 then
          redis.call('PEXPIRE', @key, @window)
          ttl = tonumber(@window)
        end
        return { count, ttl }
        """);

    public async Task<RateLimitCount?> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        try
        {
            var multiplexer = await connection.Value.ConfigureAwait(false);
            if (!multiplexer.IsConnected)
            {
                // Fail open without waiting: an unavailable cache must not stop the API.
                LogUnavailable(logger);
                RateLimitMetrics.StoreErrors.Add(1);
                return null;
            }

            var result = (RedisResult[]?)await multiplexer.GetDatabase()
                .ScriptEvaluateAsync(Hit, new { key = (RedisKey)key, window = (long)window.TotalMilliseconds })
                .ConfigureAwait(false);
            return result is [var count, var ttl]
                ? new RateLimitCount((long)count, TimeSpan.FromMilliseconds(Math.Max(0, (long)ttl)))
                : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogFailed(logger, ex.Message);
            RateLimitMetrics.StoreErrors.Add(1);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limits are not applied: the cache (Valkey) is not connected.")]
    private static partial void LogUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limits are not applied: the cache (Valkey) failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}

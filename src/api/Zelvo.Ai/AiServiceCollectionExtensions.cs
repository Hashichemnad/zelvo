using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zelvo.Data;
using Zelvo.Integrations;

namespace Zelvo.Ai;

/// <summary>Registers the AI gateway for the API and the worker.</summary>
public static class AiServiceCollectionExtensions
{
    /// <summary>The gateway and its provider client. Needs <c>AddZelvoData</c> and <c>AddZelvoSecrets</c>.</summary>
    public static IServiceCollection AddZelvoAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ => AiOptions.From(configuration));
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<AiOptions>();
            return new AiProviderClient(options, new OutboundAddressPolicy(options.Network));
        });
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAiGateway, AiGateway>();
        return services;
    }

    /// <summary>The worker side: the gateway, and clearing AI log text past its retention.</summary>
    public static IServiceCollection AddZelvoAiJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoAi(configuration);
        services.TryAddSingleton<AiCallTextPurge>();
        services.AddHostedService(sp => sp.GetRequiredService<AiCallTextPurge>());
        return services;
    }
}

/// <summary>
/// Worker job: clears the masked prompt and answer of AI calls older than
/// <see cref="AiOptions.LogRetention"/> (30 days by default). Counts and cost are kept for the
/// usage dashboard.
/// </summary>
public sealed partial class AiCallTextPurge(IServiceScopeFactory scopes, AiOptions options, ILogger<AiCallTextPurge> logger) : BackgroundService
{
    /// <summary>Clears old text now; returns how many calls were cleared.</summary>
    public async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var before = DateTimeOffset.UtcNow - options.LogRetention;
        var cleared = await db.Database.SqlQuery<long>($"SELECT zelvo_purge_ai_call_text({before}) AS \"Value\"")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (cleared > 0)
        {
            LogPurged(logger, cleared);
        }

        return cleared;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PurgeInterval);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleared the text of {Count} AI calls past their retention.")]
    private static partial void LogPurged(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Clearing old AI log text failed; trying again later")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}

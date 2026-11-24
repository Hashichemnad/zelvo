using Elsa.Common.Multitenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zelvo.Workflows.Engine;

/// <summary>
/// Starts Elsa in the background instead of during the API's start-up (Elsa's own
/// <c>ActivateTenants</c> hosted service is removed): Elsa registers its code workflows and
/// recovers interrupted ones, which needs the database. The API then starts, and reports its
/// readiness, even while PostgreSQL is briefly unavailable; activation is retried until it works.
/// The approval engine waits for it.
/// </summary>
public sealed partial class ElsaActivation(IServiceScopeFactory scopes, ILogger<ElsaActivation> logger) : BackgroundService
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits until Elsa is active, at most <paramref name="timeout"/>.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _ready.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("The workflow engine is not available yet. Try again in a moment.", exception);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ITenantService>().ActivateTenantsAsync(stoppingToken).ConfigureAwait(false);
                _ready.TrySetResult();
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogActivationFailed(logger, delay, exception);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The workflow engine could not start yet; trying again in {Delay}.")]
    private static partial void LogActivationFailed(ILogger logger, TimeSpan delay, Exception exception);
}

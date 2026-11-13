namespace Zelvo.Worker;

/// <summary>
/// Writes a heartbeat file the container health check reads, so Docker knows the worker is alive.
/// </summary>
internal sealed partial class HeartbeatService(ILogger<HeartbeatService> logger, IConfiguration configuration)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = configuration["Worker:HeartbeatFile"] ?? Path.Combine(Path.GetTempPath(), "zelvo-worker.heartbeat");
        LogStarted(logger);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await File.WriteAllTextAsync(path, DateTimeOffset.UtcNow.ToString("O"), stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Zelvo worker started")]
    private static partial void LogStarted(ILogger logger);
}

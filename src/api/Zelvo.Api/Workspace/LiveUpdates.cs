using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using Zelvo.Api.Identity;
using Zelvo.Data.Live;
using Zelvo.Api.Tenancy;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Workspace;

/// <summary>
/// Live updates over SignalR (module 5, task B4: "no full page reloads", live SLA timers). Signed-in
/// people connect to <c>/api/v1/live</c>; staff who may see incidents hear which tickets changed,
/// and everyone hears when their own notifications change. Messages carry ids only: the browser
/// loads what changed through the API, which applies permissions and field rules. Server-Sent
/// Events and long polling only, because the web app's backend-for-frontend forwards the
/// connection with the session's token (the browser never holds one).
/// </summary>
[Authorize]
internal sealed class LiveHub : Hub
{
    /// <summary>Message: a ticket, its conversation or its SLA clocks changed. Argument: <c>{ ticketId }</c>.</summary>
    public const string TicketChanged = "ticketChanged";

    /// <summary>Message: many tickets changed; lists should load again. No argument.</summary>
    public const string Refresh = "refresh";

    /// <summary>Message: the person's notifications changed. No argument.</summary>
    public const string NotificationsChanged = "notificationsChanged";

    public static string StaffGroup(Guid tenant) => $"staff:{tenant:N}";

    public static string UserGroup(Guid tenant, Guid user) => $"user:{tenant:N}:{user:N}";

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        // The connection's own request: tenant resolution and the current user ran for it.
        var services = Context.GetHttpContext()?.RequestServices;
        var user = services?.GetService<CurrentUser>();
        var tenant = services?.GetService<ITenantContext>()?.TenantId;
        if (user?.Account is not { } account || tenant is not { } tenantId)
        {
            // Integrations and anonymous callers have no live updates.
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(tenantId, account.Id)).ConfigureAwait(false);
        if (user.Has(Permissions.IncidentsRead))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup(tenantId)).ConfigureAwait(false);
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Listens on PostgreSQL's <see cref="LiveUpdateInterceptor.Channel"/> and forwards each live event
/// to this instance's SignalR connections. Every API instance listens, so no SignalR backplane is
/// needed; PostgreSQL sends events only for committed changes, from the API and the worker alike.
/// </summary>
internal sealed partial class LiveListener(IHubContext<LiveHub> hub, IConfiguration configuration, ILogger<LiveListener> logger) : BackgroundService
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Postgres"))
        {
            // Notice a broken connection, then listen again.
            KeepAlive = 30,
            ApplicationName = "zelvo-api-live",
        };
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                connection.Notification += (_, e) => Forward(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {LiveUpdateInterceptor.Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                }

                failures = 0;
                LogListening(logger);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.Net.Sockets.SocketException or IOException)
            {
                // Browsers keep working without live updates (they load data on their own); listen again soon.
                LogListenFailed(logger, ex);
                await Task.Delay(Backoff[Math.Min(failures++, Backoff.Length - 1)], stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void Forward(string payload)
    {
        if (LiveEvent.Parse(payload) is not { } live)
        {
            return;
        }

        var send = live.Kind switch
        {
            LiveEvent.Ticket => hub.Clients.Group(LiveHub.StaffGroup(live.Tenant)).SendAsync(LiveHub.TicketChanged, new { ticketId = live.Id }),
            LiveEvent.RefreshAll => hub.Clients.Group(LiveHub.StaffGroup(live.Tenant)).SendAsync(LiveHub.Refresh),
            LiveEvent.Notifications when live.Id is { } user => hub.Clients.Group(LiveHub.UserGroup(live.Tenant, user)).SendAsync(LiveHub.NotificationsChanged),
            _ => Task.CompletedTask,
        };
        _ = send.ContinueWith(t => LogSendFailed(logger, t.Exception!), TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for live updates")]
    private static partial void LogListening(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live updates stopped; listening again shortly")]
    private static partial void LogListenFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live update could not be sent")]
    private static partial void LogSendFailed(ILogger logger, Exception exception);
}

/// <summary>Registers and maps live updates.</summary>
internal static class LiveUpdatesSetup
{
    public static IServiceCollection AddZelvoLiveUpdates(this IServiceCollection services)
    {
        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            // Clients only listen; they send nothing but the handshake and pings.
            options.MaximumReceiveMessageSize = 4 * 1024;
        });
        services.AddHostedService<LiveListener>();
        return services;
    }

    public static IEndpointRouteBuilder MapZelvoLiveUpdates(this IEndpointRouteBuilder app)
    {
        app.MapHub<LiveHub>("/api/v1/live", options =>
        {
            options.Transports = HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
            // A connection ends when its token expires; the browser reconnects with a fresh one.
            options.CloseOnAuthenticationExpiration = true;
        }).RequireTenant().RequireAuthorization();
        return app;
    }
}

/// <summary>Maps the agent workspace's endpoints (task B4).</summary>
internal static class WorkspaceEndpoints
{
    public static RouteGroupBuilder MapWorkspaceEndpoints(this RouteGroupBuilder group) =>
        group.MapCommentEndpoints()
            .MapCannedResponseEndpoints()
            .MapSavedViewEndpoints()
            .MapCustomerEndpoints()
            .MapBulkIncidentEndpoints();
}

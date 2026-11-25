using System.Data.Common;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zelvo.Data.Tenancy;

namespace Zelvo.Workflows.Engine;

/// <summary>Where an approval process is after the engine ran.</summary>
/// <param name="InstanceId">The workflow instance.</param>
/// <param name="WaitingLevel">The level now waiting for decisions; <see langword="null"/> when the process ended.</param>
/// <param name="BookmarkId">The bookmark of the waiting level.</param>
public sealed record ApprovalStep(string InstanceId, int? WaitingLevel, string? BookmarkId);

/// <summary>
/// Runs approval processes (task C3). Zelvo keeps approvers, decisions and outcomes in its own
/// tables; the engine keeps the process's position and waits for each level's outcome. Behind this
/// interface so that the approved fallback (Temporal) could replace Elsa.
/// </summary>
public interface IApprovalEngine
{
    /// <summary>Starts a process of <paramref name="levels"/> levels (at least one); it waits at level 0.</summary>
    Task<ApprovalStep> StartAsync(Guid tenantId, Guid processId, int levels, CancellationToken cancellationToken);

    /// <summary>Gives the outcome of the waiting level: approved moves on to the next level (or ends), rejected ends the process.</summary>
    Task<ApprovalStep> CompleteLevelAsync(Guid tenantId, string instanceId, string bookmarkId, bool approved, CancellationToken cancellationToken);

    /// <summary>Stops a waiting process.</summary>
    Task CancelAsync(Guid tenantId, string instanceId, CancellationToken cancellationToken);
}

/// <summary>
/// The approval workflow: one activity that walks the levels, waiting at a bookmark for each one's
/// outcome (input <c>Approved</c>). Its position is persisted by Elsa, so a process survives
/// restarts and continues on any instance of the API.
/// </summary>
public sealed class ApprovalLevels : Activity
{
    internal const string LevelsInput = "Levels";
    internal const string ApprovedInput = "Approved";
    private const string LevelProperty = "ZelvoLevel";

    /// <inheritdoc />
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetProperty(LevelProperty, 0);
        Wait(context, 0);
        return default;
    }

    private static void Wait(ActivityExecutionContext context, int level) =>
        context.CreateBookmark(new Dictionary<string, object> { ["level"] = level }, OnOutcomeAsync);

    private static async ValueTask OnOutcomeAsync(ActivityExecutionContext context)
    {
        var level = context.GetProperty<int>(LevelProperty);
        var levels = int.Parse(context.GetWorkflowInput<string>(LevelsInput), System.Globalization.CultureInfo.InvariantCulture);
        var approved = string.Equals(context.GetWorkflowInput<string>(ApprovedInput), "true", StringComparison.Ordinal);
        if (approved && level + 1 < levels)
        {
            context.SetProperty(LevelProperty, level + 1);
            Wait(context, level + 1);
            return;
        }

        await context.CompleteActivityAsync().ConfigureAwait(false);
    }
}

/// <summary>The code-defined approval workflow.</summary>
public sealed class ApprovalWorkflow : WorkflowBase
{
    /// <summary>Definition id.</summary>
    public const string DefinitionId = "zelvo-approval";

    /// <inheritdoc />
    protected override void Build(IWorkflowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithDefinitionId(DefinitionId);
        builder.Root = new ApprovalLevels();
    }
}

/// <summary>The approval engine on Elsa Workflows 3.</summary>
public sealed class ElsaApprovalEngine(IServiceScopeFactory scopes, ElsaActivation activation) : IApprovalEngine
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<ApprovalStep> StartAsync(Guid tenantId, Guid processId, int levels, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
        await activation.WaitAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);
        using var tenant = ElsaTenant.Enter(tenantId);
        await using var scope = scopes.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(ApprovalWorkflow.DefinitionId, Elsa.Common.Models.VersionOptions.Published),
            CorrelationId = processId.ToString(),
            Input = new Dictionary<string, object> { [ApprovalLevels.LevelsInput] = levels.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        }, cancellationToken).ConfigureAwait(false);
        return Step(client.WorkflowInstanceId, result.Bookmarks);
    }

    /// <inheritdoc />
    public async Task<ApprovalStep> CompleteLevelAsync(Guid tenantId, string instanceId, string bookmarkId, bool approved, CancellationToken cancellationToken)
    {
        await activation.WaitAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);
        using var tenant = ElsaTenant.Enter(tenantId);
        await using var scope = scopes.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var result = await client.RunInstanceAsync(new RunWorkflowInstanceRequest
        {
            BookmarkId = bookmarkId,
            Input = new Dictionary<string, object> { [ApprovalLevels.ApprovedInput] = approved ? "true" : "false" },
        }, cancellationToken).ConfigureAwait(false);
        return Step(instanceId, result.Bookmarks);
    }

    /// <inheritdoc />
    public async Task CancelAsync(Guid tenantId, string instanceId, CancellationToken cancellationToken)
    {
        await activation.WaitAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);
        using var tenant = ElsaTenant.Enter(tenantId);
        await using var scope = scopes.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync(instanceId, cancellationToken).ConfigureAwait(false);
        await client.CancelAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ApprovalStep Step(string instanceId, ICollection<Elsa.Workflows.Models.Bookmark> bookmarks)
    {
        var bookmark = bookmarks.FirstOrDefault();
        if (bookmark is null)
        {
            return new ApprovalStep(instanceId, null, null);
        }

        var level = bookmark.Payload switch
        {
            IDictionary<string, object> payload when payload.TryGetValue("level", out var value) => value switch
            {
                System.Text.Json.JsonElement element when element.TryGetInt32(out var n) => n,
                IConvertible convertible => convertible.ToInt32(System.Globalization.CultureInfo.InvariantCulture),
                _ => 0,
            },
            System.Text.Json.JsonElement element when element.TryGetProperty("level", out var property) && property.TryGetInt32(out var n) => n,
            _ => 0,
        };
        return new ApprovalStep(instanceId, level, bookmark.Id);
    }
}

/// <summary>
/// The tenant Elsa works for. Elsa's tables carry a <c>zelvo_tenant_id</c> column filled by the
/// database from the connection's <c>zelvo.tenant_id</c> setting and are guarded by forced
/// row-level security, like every tenant-owned table of Zelvo.
/// </summary>
public static class ElsaTenant
{
    private static readonly AsyncLocal<Guid?> Current = new();

    /// <summary>The tenant of the current flow, if any.</summary>
    public static Guid? TenantId => Current.Value;

    /// <summary>Works for <paramref name="tenantId"/> until the result is disposed.</summary>
    public static IDisposable Enter(Guid tenantId)
    {
        var previous = Current.Value;
        Current.Value = tenantId;
        return new Restore(previous);
    }

    private sealed class Restore(Guid? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Sets <c>zelvo.tenant_id</c> on every connection Elsa opens, from <see cref="ElsaTenant"/>.</summary>
public sealed class ElsaTenantConnectionInterceptor : DbConnectionInterceptor
{
    private const string Sql = "SELECT set_config('" + TenantConnectionInterceptor.SettingName + "', @tenant, false)";

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Command(connection);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlCommand Command(DbConnection connection)
    {
        var command = new NpgsqlCommand(Sql, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("tenant", ElsaTenant.TenantId?.ToString() ?? string.Empty);
        return command;
    }
}

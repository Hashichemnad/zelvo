using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using Zelvo.Data.Channels;
using Zelvo.Data.Notifications;
using Zelvo.Data.Sla;
using Zelvo.Data.Tickets;

namespace Zelvo.Data.Live;

/// <summary>
/// Live updates of the agent workspace (module 5, task B4). After a save that changed tickets, their
/// conversation or SLA clocks, or someone's notifications, it sends a PostgreSQL notification on
/// <see cref="Channel"/>. PostgreSQL delivers notifications only when the transaction commits (and
/// drops them on rollback), so listeners never hear of changes that were not saved. Every API
/// instance listens and forwards to its own SignalR connections, and the worker's changes (SLA
/// timers, auto-close, delivered notifications) reach browsers the same way.
/// </summary>
/// <remarks>
/// Payloads hold ids only, never ticket content or personal data: browsers load what changed
/// through the API, which applies their permissions and field rules.
/// </remarks>
public sealed class LiveUpdateInterceptor : SaveChangesInterceptor
{
    /// <summary>The PostgreSQL notification channel.</summary>
    public const string Channel = "zelvo_live";

    /// <summary>More tickets than this in one save are announced as one "refresh" for the tenant.</summary>
    internal const int MaxTicketsPerSave = 200;

    private readonly List<object> _pending = [];

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context && Take() is { Length: > 0 } payloads)
        {
            context.Database.ExecuteSqlRaw(Sql, Parameter(payloads));
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context && Take() is { Length: > 0 } payloads)
        {
            // Inside a transaction the notification waits for the commit; otherwise the save is already committed.
            await context.Database.ExecuteSqlRawAsync(Sql, [Parameter(payloads)], cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _pending.Clear();

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return Task.CompletedTask;
    }

    private const string Sql = "SELECT pg_notify('" + Channel + "', p) FROM unnest(@payloads) AS p";

    private static NpgsqlParameter Parameter(string[] payloads) =>
        new("payloads", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = payloads };

    // Entities are kept until the save is done: tenant ids of new rows are stamped during the save.
    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            var interesting = entry.Entity switch
            {
                Ticket or Incident or TicketComment or TicketSla or SlaAlert => entry.State is EntityState.Added or EntityState.Modified,
                // Task E2: the email of a reply going out (its delivery shows in the conversation), and live chats.
                OutboundEmail => entry.State is EntityState.Added or EntityState.Modified,
                ChatSession or ChatMessage => entry.State is EntityState.Added or EntityState.Modified,
                Notification => entry.State == EntityState.Modified
                    && (entry.Property(nameof(Notification.Status)).IsModified || entry.Property(nameof(Notification.ReadAt)).IsModified),
                _ => false,
            };
            if (interesting)
            {
                _pending.Add(entry.Entity);
            }
        }
    }

    private string[] Take()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var tickets = new HashSet<(Guid Tenant, Guid Ticket)>();
        var people = new HashSet<(Guid Tenant, Guid User)>();
        var chats = new HashSet<(Guid Tenant, Guid Chat)>();
        foreach (var entity in _pending)
        {
            switch (entity)
            {
                case Ticket t: tickets.Add((t.TenantId, t.Id)); break;
                case Incident i: tickets.Add((i.TenantId, i.TicketId)); break;
                case TicketComment c: tickets.Add((c.TenantId, c.TicketId)); break;
                case TicketSla s: tickets.Add((s.TenantId, s.TicketId)); break;
                case SlaAlert a: tickets.Add((a.TenantId, a.TicketId)); break;
                case Notification n: people.Add((n.TenantId, n.RecipientId)); break;
                case OutboundEmail o: tickets.Add((o.TenantId, o.TicketId)); break;
                case ChatSession c: chats.Add((c.TenantId, c.Id)); break;
                case ChatMessage m: chats.Add((m.TenantId, m.SessionId)); break;
            }
        }

        _pending.Clear();
        tickets.RemoveWhere(t => t.Tenant == Guid.Empty);
        people.RemoveWhere(p => p.Tenant == Guid.Empty);
        chats.RemoveWhere(c => c.Tenant == Guid.Empty);
        var payloads = new List<string>();
        foreach (var tenant in tickets.GroupBy(t => t.Tenant))
        {
            payloads.AddRange(tenant.Count() > MaxTicketsPerSave
                ? [LiveEvent.Refresh(tenant.Key).ToJson()]
                : tenant.Select(t => LiveEvent.TicketChanged(t.Tenant, t.Ticket).ToJson()));
        }

        payloads.AddRange(people.Select(p => LiveEvent.NotificationsChanged(p.Tenant, p.User).ToJson()));
        payloads.AddRange(chats.Select(c => LiveEvent.ChatChanged(c.Tenant, c.Chat).ToJson()));
        return [.. payloads];
    }
}

/// <summary>One live update, as sent on <see cref="LiveUpdateInterceptor.Channel"/>.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Kind"><c>ticket</c>, <c>refresh</c> (many tickets), <c>notifications</c> or <c>chat</c>.</param>
/// <param name="Id">The ticket (<c>ticket</c>), the person (<c>notifications</c>) or the live chat (<c>chat</c>).</param>
public sealed record LiveEvent(Guid Tenant, string Kind, Guid? Id)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A ticket, its conversation or its SLA clocks changed.</summary>
    public const string Ticket = "ticket";

    /// <summary>Many tickets changed at once; lists should load again.</summary>
    public const string RefreshAll = "refresh";

    /// <summary>A person's notifications changed (new, read).</summary>
    public const string Notifications = "notifications";

    /// <summary>A live chat changed: its state or messages (task E2).</summary>
    public const string Chat = "chat";

    /// <summary>A change of the live chat <paramref name="chat"/>.</summary>
    public static LiveEvent ChatChanged(Guid tenant, Guid chat) => new(tenant, Chat, chat);

    /// <summary>A change of <paramref name="ticket"/>.</summary>
    public static LiveEvent TicketChanged(Guid tenant, Guid ticket) => new(tenant, Ticket, ticket);

    /// <summary>Many changes in <paramref name="tenant"/>.</summary>
    public static LiveEvent Refresh(Guid tenant) => new(tenant, RefreshAll, null);

    /// <summary>A change of <paramref name="user"/>'s notifications.</summary>
    public static LiveEvent NotificationsChanged(Guid tenant, Guid user) => new(tenant, Notifications, user);

    /// <summary>The JSON payload.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a payload; <see langword="null"/> when it is not a live event.</summary>
    public static LiveEvent? Parse(string payload)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<LiveEvent>(payload, Json);
            return parsed is { Kind: Ticket or RefreshAll or Notifications or Chat } && parsed.Tenant != Guid.Empty ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

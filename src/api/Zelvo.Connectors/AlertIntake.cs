using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Connectors;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Integrations;
using Zelvo.Notifications;
using Zelvo.Tickets;

namespace Zelvo.Connectors;

/// <summary>Impact and urgency (1 = highest) an alert's severity gives.</summary>
public sealed record AlertLevel(int Impact, int Urgency);

/// <summary>
/// Where a generic alert's fields are (dotted JSON paths) and what its severities mean. Presets for
/// Zabbix and Datadog fill these in; any other tool can be mapped on screen.
/// </summary>
public sealed record AlertMapping
{
    /// <summary>Path of a list of alerts in one request; empty: the request is one alert.</summary>
    public string? AlertsPath { get; init; }

    /// <summary>The alert's identity at the source.</summary>
    public string KeyPath { get; init; } = "id";

    /// <summary>Its status.</summary>
    public string StatusPath { get; init; } = "status";

    /// <summary>Statuses that mean the alert cleared (compared without case).</summary>
    public IReadOnlyList<string> ResolvedValues { get; init; } = ["resolved", "ok", "recovered"];

    /// <summary>Title.</summary>
    public string TitlePath { get; init; } = "title";

    /// <summary>Description.</summary>
    public string? DescriptionPath { get; init; } = "message";

    /// <summary>Severity.</summary>
    public string? SeverityPath { get; init; } = "severity";

    /// <summary>Impact and urgency per severity (compared without case).</summary>
    public IReadOnlyDictionary<string, AlertLevel> Severities { get; init; } = new Dictionary<string, AlertLevel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>For severities not listed.</summary>
    public AlertLevel DefaultLevel { get; init; } = new(2, 2);

    /// <summary>Prometheus Alertmanager (its severity label).</summary>
    public static AlertMapping Alertmanager { get; } = new()
    {
        Severities = new Dictionary<string, AlertLevel>(StringComparer.OrdinalIgnoreCase) { ["critical"] = new(1, 1), ["warning"] = new(2, 2), ["info"] = new(3, 3) },
    };

    /// <summary>Zabbix's webhook media type with Zelvo's parameters (eventId, status, severity, subject, message).</summary>
    public static AlertMapping Zabbix { get; } = new()
    {
        KeyPath = "eventId", StatusPath = "status", ResolvedValues = ["resolved", "ok"], TitlePath = "subject", DescriptionPath = "message", SeverityPath = "severity",
        Severities = new Dictionary<string, AlertLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["disaster"] = new(1, 1), ["high"] = new(1, 2), ["average"] = new(2, 2), ["warning"] = new(2, 3), ["information"] = new(3, 3), ["not classified"] = new(3, 3),
        },
    };

    /// <summary>Datadog's webhook integration with Zelvo's payload ($ALERT_ID, $ALERT_TRANSITION, $EVENT_TITLE, $EVENT_MSG, $ALERT_PRIORITY).</summary>
    public static AlertMapping Datadog { get; } = new()
    {
        KeyPath = "alertId", StatusPath = "transition", ResolvedValues = ["recovered"], TitlePath = "title", DescriptionPath = "message", SeverityPath = "priority",
        Severities = new Dictionary<string, AlertLevel>(StringComparer.OrdinalIgnoreCase) { ["p1"] = new(1, 1), ["p2"] = new(1, 2), ["p3"] = new(2, 2), ["p4"] = new(3, 2), ["p5"] = new(3, 3) },
    };

    /// <summary>Reads a stored mapping.</summary>
    public static AlertMapping Read(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<AlertMapping>(json, ConnectorSettings.JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>As stored.</summary>
    public string Write() => JsonSerializer.Serialize(this, ConnectorSettings.JsonOptions);

    /// <summary>What is wrong with it.</summary>
    public IEnumerable<(string Field, string Message)> Problems()
    {
        if (string.IsNullOrWhiteSpace(KeyPath) || string.IsNullOrWhiteSpace(StatusPath) || string.IsNullOrWhiteSpace(TitlePath))
        {
            yield return ("mapping", "Name the fields of the alert's id, status and title.");
        }

        if (Severities.Values.Append(DefaultLevel).Any(l => l.Impact is < 1 or > 3 || l.Urgency is < 1 or > 3))
        {
            yield return ("mapping.severities", "Impact and urgency are 1 (high) to 3 (low).");
        }
    }
}

/// <summary>What an alert request did.</summary>
public sealed record AlertOutcome(int Created, int Updated, int Resolved, int Ignored);

/// <summary>
/// Turns alerts of a monitoring source into incidents (module 14, task E7; product owner decision
/// 2026-11-25): a new alert raises an incident (channel Monitoring, the source's category or group,
/// impact and urgency from its severity, the source's own account as requester); the same alert
/// firing again while it fires adds an internal note to its incident; clearing adds a note and,
/// when the source is set to, resolves the incident. An alert whose incident was closed raises a
/// new one. Run with the source's tenant set.
/// </summary>
public sealed class AlertIntake(ZelvoDbContext db, IncidentIntake incidents, IAuditLog audit, TimeProvider time)
{
    /// <summary>The SHA-256 (hex) of a source's token.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    /// <summary>Processes a request of <paramref name="source"/>.</summary>
    public async Task<AlertOutcome> ProcessAsync(MonitoringSource source, JsonNode payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payload);
        var mapping = source.Format == AlertFormat.Alertmanager ? AlertMapping.Alertmanager : AlertMapping.Read(source.Mapping);
        var alerts = Alerts(source.Format, mapping, payload);
        var now = time.GetUtcNow();
        var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken).ConfigureAwait(false);
        var language = await db.TenantSettings.AsNoTracking().Where(s => s.Key == "localization.defaultLanguage").Select(s => s.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            is "\"ar\"" ? LocalizedText.Arabic : LocalizedText.English;
        var terms = NotificationTexts.For(language).Terms;
        int created = 0, updated = 0, resolved = 0, ignored = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var alert in alerts)
        {
            if (alert.Key is not { Length: > 0 } key)
            {
                ignored++;
                continue;
            }

            key = key.Length <= MonitoringAlert.KeyMaxLength ? key : key[..MonitoringAlert.KeyMaxLength];
            var current = await db.MonitoringAlerts.FirstOrDefaultAsync(a => a.SourceId == source.Id && a.Key == key && a.State == AlertState.Firing, cancellationToken).ConfigureAwait(false);
            var ticket = current is null ? null : await db.Tickets.Include(t => t.Incident).FirstOrDefaultAsync(t => t.Id == current.TicketId, cancellationToken).ConfigureAwait(false);
            if (ticket is { State: TicketState.Closed } && current is not null)
            {
                // Its incident is closed: the alert's next firing starts over.
                current.State = AlertState.Resolved;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                current = null;
                ticket = null;
            }

            if (alert.Resolved)
            {
                if (current is null || ticket is null)
                {
                    ignored++;
                    continue;
                }

                current.State = AlertState.Resolved;
                current.LastSeenAt = now;
                Note(source, ticket, $"{terms.GetValueOrDefault("monitoring.resolved")}: {alert.Title}", language, now);
                if (source.ResolveOnClear && TicketWorkflow.Check(ticket, TicketState.Resolved, null, terms.GetValueOrDefault("monitoring.resolved")) == TransitionError.None)
                {
                    TicketWorkflow.Apply(ticket, TicketState.Resolved, null, $"{terms.GetValueOrDefault("monitoring.resolved")}: {alert.Title}", now);
                    await incidents.UpdateClocksAsync([ticket], now, cancellationToken).ConfigureAwait(false);
                }

                ticket.UpdatedAt = now;
                await incidents.IndexAsync([ticket], cancellationToken).ConfigureAwait(false);
                resolved++;
                continue;
            }

            if (current is not null && ticket is not null)
            {
                current.Count++;
                current.LastSeenAt = now;
                Note(source, ticket, $"{(terms.GetValueOrDefault("monitoring.fired-again") ?? string.Empty).Replace("{count}", current.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)}: {alert.Title}",
                    language, now);
                ticket.UpdatedAt = now;
                updated++;
                continue;
            }

            var level = alert.Severity is { } severity && mapping.Severities.TryGetValue(severity, out var mapped) ? mapped : mapping.DefaultLevel;
            var incident = await incidents.CreateAsync(source.TenantId, new IncidentIntakeRequest(
                alert.Title, alert.Description, TicketChannel.Monitoring, source.SystemUserId, CategoryId: source.CategoryId, GroupId: source.GroupId), now, cancellationToken).ConfigureAwait(false);
            // Saved first: the severity changes the priority, and the clocks follow it.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            incident.LocationId = source.LocationId;
            incident.Incident!.Impact = Math.Clamp(level.Impact, 1, matrix.Impacts.Count);
            incident.Incident.Urgency = Math.Clamp(level.Urgency, 1, matrix.Urgencies.Count);
            incident.Incident.Priority = matrix.PriorityOf(incident.Incident.Impact, incident.Incident.Urgency);
            await incidents.UpdateClocksAsync([incident], now, cancellationToken).ConfigureAwait(false);
            db.MonitoringAlerts.Add(new MonitoringAlert { TenantId = source.TenantId, SourceId = source.Id, Key = key, TicketId = incident.Id, State = AlertState.Firing, FirstSeenAt = now, LastSeenAt = now });
            created++;
        }

        source.LastAlertAt = now;
        audit.Add(AuditCategory.Change, "monitoring.alerts.received", "monitoring_source", source.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["created"] = created, ["updated"] = updated, ["resolved"] = resolved, ["ignored"] = ignored,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AlertOutcome(created, updated, resolved, ignored);
    }

    private void Note(MonitoringSource source, Ticket ticket, string text, string language, DateTimeOffset now) =>
        db.TicketComments.Add(new TicketComment
        {
            TenantId = source.TenantId, TicketId = ticket.Id, Visibility = CommentVisibility.Internal, Body = IncidentIntake.Clean(text, TicketComment.BodyMaxLength), Language = language,
            AuthorId = source.SystemUserId, ByStaff = true, CreatedAt = now,
        });

    private sealed record Alert(string? Key, bool Resolved, string Title, string? Description, string? Severity);

    private static List<Alert> Alerts(AlertFormat format, AlertMapping mapping, JsonNode payload)
    {
        var result = new List<Alert>();
        if (format == AlertFormat.Alertmanager)
        {
            foreach (var a in payload["alerts"] as JsonArray ?? [])
            {
                var labels = a?["labels"] as JsonObject;
                var key = Text(a, "fingerprint") ?? (labels is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(labels.ToJsonString())))[..16]);
                var name = Text(labels, "alertname") ?? "alert";
                var summary = Text(a?["annotations"], "summary");
                var instance = Text(labels, "instance");
                var title = summary ?? (instance is null ? name : $"{name} ({instance})");
                result.Add(new Alert(key, string.Equals(Text(a, "status"), "resolved", StringComparison.OrdinalIgnoreCase), Title(title),
                    Text(a?["annotations"], "description") ?? Text(a, "generatorURL"), Text(labels, "severity")));
            }

            return result;
        }

        var items = mapping.AlertsPath is { Length: > 0 } listPath ? Find(payload, listPath) as JsonArray ?? [] : new JsonArray(payload.DeepClone());
        foreach (var item in items)
        {
            var status = Text(Find(item, mapping.StatusPath));
            result.Add(new Alert(Text(Find(item, mapping.KeyPath)), status is not null && mapping.ResolvedValues.Contains(status, StringComparer.OrdinalIgnoreCase),
                Title(Text(Find(item, mapping.TitlePath)) ?? "alert"), mapping.DescriptionPath is { } d ? Text(Find(item, d)) : null,
                mapping.SeverityPath is { } s ? Text(Find(item, s)) : null));
        }

        return result;
    }

    private static string Title(string value)
    {
        var line = value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "alert";
        return line.Length <= 200 ? line : line[..199] + "…";
    }

    private static JsonNode? Find(JsonNode? root, string path)
    {
        var node = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(part, out var child) ? child : null,
                JsonArray a when int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
        }

        return node;
    }

    private static string? Text(JsonNode? node, string name) => Text(node?[name]);

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0 ? s : null,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };
}

/// <summary>Registers connectors for the API and the worker.</summary>
public static class ConnectorsServiceCollectionExtensions
{
    /// <summary>What both hosts need: options, clients, sync engines, the outbox and alert intake.</summary>
    public static IServiceCollection AddZelvoConnectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ => ConnectorOptions.Read(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ConnectorClients(sp.GetRequiredService<ConnectorOptions>(), new OutboundAddressPolicy(sp.GetRequiredService<ConnectorOptions>().Network)));
        services.TryAddSingleton<IConnectorClients>(sp => sp.GetRequiredService<ConnectorClients>());
        services.TryAddScoped<DirectorySync>();
        services.TryAddScoped<CrmSync>();
        services.TryAddScoped<RestRunner>();
        services.TryAddScoped<ConnectorCallSender>();
        services.TryAddScoped<ConnectorSyncRunner>();
        services.TryAddScoped<AlertIntake>();
        if (!services.Any(d => d.ImplementationType == typeof(ConnectorOutboxInterceptor)))
        {
            services.AddZelvoSaveChangesInterceptor<ConnectorOutboxInterceptor>();
        }

        return services;
    }

    /// <summary>The worker side: syncs and calls.</summary>
    public static IServiceCollection AddZelvoConnectorJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoConnectors(configuration);
        services.TryAddSingleton<ConnectorJob>();
        services.AddHostedService(sp => sp.GetRequiredService<ConnectorJob>());
        return services;
    }
}

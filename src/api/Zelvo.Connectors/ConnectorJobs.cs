using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Connectors;
using Zelvo.Data.Integrations;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Integrations;
using Zelvo.Notifications;
using Zelvo.Secrets;
using Zelvo.Tickets;

namespace Zelvo.Connectors;

/// <summary>A connector's credential, encrypted with the tenant's data key under a purpose of its own.</summary>
public static class ConnectorSecrets
{
    /// <summary>Purpose of a connector's credential.</summary>
    public static string Purpose(Guid connectorId) => $"connector:{connectorId:N}.credential";

    /// <summary>Decrypts <paramref name="connector"/>'s credential, if it has one.</summary>
    public static async Task<string?> ReadAsync(ISecretProtector secrets, Connector connector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(connector);
        return connector.CredentialCiphertext is { } ciphertext ? await secrets.UnprotectAsync(Purpose(connector.Id), ciphertext, cancellationToken).ConfigureAwait(false) : null;
    }
}

/// <summary>
/// Queues CRM case updates with the change (task E7): a ticket created for a requester who is a
/// contact of an active CRM connector, or whose state changes, gets a <see cref="ConnectorCall"/>
/// in the same transaction, so the case is updated if and only if the change was saved.
/// </summary>
public sealed class ConnectorOutboxInterceptor(ITenantContext tenantContext) : SaveChangesInterceptor, IZelvoSaveChangesInterceptor
{
    private bool _collecting;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        QueueAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await QueueAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task QueueAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (_collecting || context is not ZelvoDbContext db || tenantContext.TenantId is not { } tenantId)
        {
            return;
        }

        db.ChangeTracker.DetectChanges();
        var changes = db.ChangeTracker.Entries<Ticket>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified && e.Property(t => t.State).IsModified)
            .Select(e => (e.Entity, Created: e.State == EntityState.Added)).ToList();
        if (changes.Count == 0)
        {
            return;
        }

        _collecting = true;
        try
        {
            var crm = await db.Connectors.AsNoTracking()
                .Where(c => c.Active && (c.Kind == ConnectorKind.Dynamics365 || c.Kind == ConnectorKind.Salesforce || c.Kind == ConnectorKind.HubSpot || c.Kind == ConnectorKind.Zoho))
                .Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (crm.Count == 0)
            {
                return;
            }

            var requesters = changes.Select(c => c.Entity.RequesterId).Distinct().ToList();
            var contacts = await db.ConnectorLinks.AsNoTracking()
                .Where(l => crm.Contains(l.ConnectorId) && l.Entity == ConnectorEntity.User && requesters.Contains(l.ZelvoId))
                .Select(l => new { l.ConnectorId, l.ZelvoId }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            foreach (var (ticket, created) in changes)
            {
                foreach (var link in contacts.Where(l => l.ZelvoId == ticket.RequesterId))
                {
                    db.ConnectorCalls.Add(new ConnectorCall
                    {
                        TenantId = tenantId, ConnectorId = link.ConnectorId, Kind = ConnectorCallKind.CrmCase, Operation = created ? "created" : "state",
                        TicketId = ticket.Id, CreatedAt = now, NextAttemptAt = now,
                    });
                }
            }
        }
        finally
        {
            _collecting = false;
        }
    }
}

/// <summary>A REST operation's answer as the test button shows it.</summary>
/// <param name="Ok">2xx.</param>
/// <param name="Status">HTTP status, when there was an answer.</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="Request">What was sent: method, address and body (personal data masked).</param>
/// <param name="Response">The answer (masked, cut).</param>
/// <param name="Results">Values read from the answer.</param>
/// <param name="ExternalRef">The other system's id of the record, when the operation reads one.</param>
/// <param name="Error">Why it failed.</param>
public sealed record RestOutcome(
    bool Ok, int? Status, int DurationMs, string Request, string? Response, IReadOnlyDictionary<string, string?> Results, string? ExternalRef, string? Error);

/// <summary>Runs REST operations: fills in the placeholders from a ticket, sends, reads the answer.</summary>
public sealed class RestRunner(ZelvoDbContext db, IConnectorClients clients, ConnectorOptions options, ITenantContext tenant)
{
    /// <summary>The placeholder values of <paramref name="ticket"/> (null: sample values for the test button).</summary>
    public async Task<Dictionary<string, string?>> ValuesAsync(Guid connectorId, Ticket? ticket, CancellationToken cancellationToken)
    {
        var language = await db.TenantSettings.AsNoTracking().Where(s => s.Key == "localization.defaultLanguage").Select(s => s.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            is "\"ar\"" ? LocalizedText.Arabic : LocalizedText.English;
        var tenantName = (await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false))
            ?.GetOrFallback(language);
        if (ticket is null)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ticket.id"] = Guid.Empty.ToString(), ["ticket.number"] = "INC-000123", ["ticket.title"] = "Sample ticket", ["ticket.description"] = "Sample description",
                ["ticket.state"] = "InProgress", ["ticket.type"] = "Incident", ["ticket.priority"] = "P3", ["ticket.channel"] = "Portal", ["ticket.group"] = "Service desk",
                ["ticket.category"] = "Hardware", ["ticket.url"] = new Uri(options.WebUrl, "en/incidents/00000000-0000-0000-0000-000000000000").AbsoluteUri,
                ["ticket.createdAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["ticket.resolutionNotes"] = null, ["ticket.externalRef"] = "EXT-1",
                ["requester.name"] = "Sample requester", ["requester.email"] = "requester@example.com", ["requester.phone"] = "+971500000000", ["requester.employeeId"] = "1001",
                ["tenant.name"] = tenantName,
            };
        }

        var requester = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ticket.RequesterId, cancellationToken).ConfigureAwait(false);
        var group = ticket.GroupId is { } g ? await db.SupportGroups.AsNoTracking().Where(x => x.Id == g).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) : null;
        var category = ticket.CategoryId is { } c ? await db.Categories.AsNoTracking().Where(x => x.Id == c).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) : null;
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.TicketId == ticket.Id, cancellationToken).ConfigureAwait(false);
        var reference = await db.ConnectorLinks.AsNoTracking().Where(l => l.ConnectorId == connectorId && l.Entity == ConnectorEntity.Ticket && l.ZelvoId == ticket.Id)
            .Select(l => l.ExternalId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var page = ticket.Type switch { TicketType.ServiceRequest => "requests", TicketType.Incident => "incidents", _ => "voice" };
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ticket.id"] = ticket.Id.ToString(), ["ticket.number"] = ticket.Number, ["ticket.title"] = ticket.Title, ["ticket.description"] = ticket.Description,
            ["ticket.state"] = ticket.State.ToString(), ["ticket.type"] = ticket.Type.ToString(), ["ticket.priority"] = incident is null ? null : $"P{incident.Priority}",
            ["ticket.channel"] = ticket.Channel.ToString(), ["ticket.group"] = group?.GetOrFallback(language), ["ticket.category"] = category?.GetOrFallback(language),
            ["ticket.url"] = new Uri(options.WebUrl, $"{language}/{page}/{ticket.Id}").AbsoluteUri, ["ticket.createdAt"] = ticket.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["ticket.resolutionNotes"] = ticket.ResolutionNotes, ["ticket.externalRef"] = reference,
            ["requester.name"] = requester?.DisplayName, ["requester.email"] = requester?.Email, ["requester.phone"] = requester?.Phone, ["requester.employeeId"] = requester?.EmployeeId,
            ["tenant.name"] = tenantName,
        };
    }

    /// <summary>Renders and sends <paramref name="operation"/>; never throws for the other system's refusals.</summary>
    public async Task<RestOutcome> RunAsync(Connector connector, RestSettings settings, string? credential, RestOperation operation, IReadOnlyDictionary<string, string?> values,
        ExchangeLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(operation);
        var path = RestSettings.Render(operation.Path, values, Uri.EscapeDataString);
        var baseUrl = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        var url = new Uri(baseUrl, path.TrimStart('/'));
        // Placeholders cannot move the call to another host or above the base path.
        if (!string.Equals(url.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase) || !url.AbsolutePath.StartsWith(baseUrl.AbsolutePath, StringComparison.Ordinal))
        {
            return new RestOutcome(false, null, 0, $"{operation.Method} {url.GetLeftPart(UriPartial.Path)}", null, new Dictionary<string, string?>(), null, "path-outside-base");
        }

        var body = operation.Body is { Length: > 0 } template && operation.Method is not ("GET" or "DELETE")
            ? RestSettings.Render(template, values, v => JsonEncodedText.Encode(v, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value)
            : null;
        var request = $"{operation.Method} {url.GetLeftPart(UriPartial.Path)}{PersonalDataMasker.MaskQueryString(url.Query)}"
            + (body is null ? string.Empty : "\n\n" + PersonalDataMasker.MaskBody(body, "application/json", 8000));
        try
        {
            var answer = await clients.CallAsync(connector, settings, credential, new RestCall(operation.Method, url, settings.Headers, body), log, cancellationToken).ConfigureAwait(false);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(answer.Body) ? null : JsonNode.Parse(answer.Body);
            }
            catch (JsonException)
            {
                json = null;
            }

            var results = operation.Results.ToDictionary(r => r.Key, r => Limit(JsonPathText(json, r.Path)), StringComparer.Ordinal);
            var reference = operation.ExternalRefPath is { Length: > 0 } refPath ? Limit(JsonPathText(json, refPath)) : null;
            var ok = answer.Status is >= 200 and < 300;
            return new RestOutcome(ok, answer.Status, answer.DurationMs, request, PersonalDataMasker.MaskBody(answer.Body, answer.ContentType, 4000), results, reference,
                ok ? null : ConnectorClients.Error(answer.Status, url.Host, answer.Body).Summary);
        }
        catch (ConnectorException ex)
        {
            return new RestOutcome(false, null, 0, request, null, new Dictionary<string, string?>(), null, ex.Summary);
        }
    }

    private static string? JsonPathText(JsonNode? root, string path)
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

        return node switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString(),
        };
    }

    private static string? Limit(string? value) => value is null ? null : value.Length <= 500 ? value : value[..499] + "…";
}

/// <summary>
/// Sends one queued connector call (scoped; tenant set): a CRM case created or updated with a note,
/// or a REST operation whose answer becomes an internal note on the ticket. Retries after the
/// connectors' delays; refusals that will not improve fail at once. Every exchange is in the
/// integration log with personal data masked.
/// </summary>
public sealed partial class ConnectorCallSender(
    ZelvoDbContext db, ISecretProtector secrets, IConnectorClients clients, RestRunner rest, ConnectorOptions options, TimeProvider time, ILogger<ConnectorCallSender> logger)
{
    /// <summary>Sends call <paramref name="id"/> if it still waits.</summary>
    public async Task SendAsync(long id, CancellationToken cancellationToken)
    {
        var call = await db.ConnectorCalls.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (call is not { Status: ConnectorCallStatus.Pending })
        {
            return;
        }

        var connector = await db.Connectors.FirstOrDefaultAsync(c => c.Id == call.ConnectorId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        call.LeaseUntil = null;
        if (connector is not { Active: true })
        {
            call.NextAttemptAt = now + options.RetryDelays[^1];
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var ticket = call.TicketId is { } ticketId ? await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken).ConfigureAwait(false) : null;
        var settings = ConnectorSettings.Read(connector.Settings);
        var log = new ExchangeLog();
        call.Attempts++;
        string? error = null;
        var permanent = false;
        try
        {
            var credential = await ConnectorSecrets.ReadAsync(secrets, connector, cancellationToken).ConfigureAwait(false);
            if (ticket is null)
            {
                throw new ConnectorException("ticket-gone", "The ticket no longer exists.", true);
            }

            if (call.Kind == ConnectorCallKind.CrmCase)
            {
                await CrmAsync(connector, settings, credential, call, ticket, log, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RestAsync(connector, settings, credential, call, ticket, log, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ConnectorException ex)
        {
            error = ex.Summary;
            permanent = ex.Permanent;
        }
        catch (SecretDecryptionException)
        {
            error = "key-unreadable: The stored credential cannot be decrypted. Enter it again.";
            permanent = true;
        }

        var last = log.Exchanges.LastOrDefault();
        call.StatusCode = last?.Status;
        call.DurationMs = log.Exchanges.Sum(e => e.DurationMs);
        if (error is null)
        {
            call.Status = ConnectorCallStatus.Succeeded;
            call.Error = null;
            call.FinishedAt = now;
        }
        else
        {
            LogCallFailed(logger, call.Id, call.Attempts, error.Split(':')[0]);
            var giveUp = permanent || call.Attempts > options.RetryDelays.Count;
            call.Status = giveUp ? ConnectorCallStatus.Failed : ConnectorCallStatus.Pending;
            call.NextAttemptAt = giveUp ? null : now + options.RetryDelays[call.Attempts - 1];
            call.FinishedAt = giveUp ? now : null;
            call.Error = PersonalDataMasker.MaskText(error.Length <= ConnectorCall.ErrorMaxLength ? error : error[..ConnectorCall.ErrorMaxLength]);
            connector.LastError = call.Error;
        }

        foreach (var exchange in log.Exchanges)
        {
            db.IntegrationLog.Add(new IntegrationLogEntry
            {
                TenantId = connector.TenantId,
                OccurredAt = now,
                Direction = IntegrationDirection.Outgoing,
                EventType = $"connector.{connector.Kind.ToString().ToLowerInvariant()}.{call.Operation}",
                Method = exchange.Method,
                Target = Shorten(new Uri(exchange.Url).GetLeftPart(UriPartial.Path) + PersonalDataMasker.MaskQueryString(new Uri(exchange.Url).Query), IntegrationLogEntry.TargetMaxLength),
                StatusCode = exchange.Status,
                DurationMs = exchange.DurationMs,
                RequestBody = PersonalDataMasker.MaskBody(exchange.RequestBody, exchange.RequestType ?? "application/json", IntegrationLogEntry.BodyMaxLength),
                ResponseBody = PersonalDataMasker.MaskBody(exchange.ResponseBody, exchange.ResponseType, IntegrationLogEntry.BodyMaxLength),
                Error = exchange.Error is null ? null : Shorten(PersonalDataMasker.MaskText(exchange.Error), IntegrationLogEntry.ErrorMaxLength),
                CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CrmAsync(Connector connector, ConnectorSettings settings, string? credential, ConnectorCall call, Ticket ticket, ExchangeLog log, CancellationToken cancellationToken)
    {
        var contact = await db.ConnectorLinks.AsNoTracking().FirstOrDefaultAsync(l => l.ConnectorId == connector.Id && l.Entity == ConnectorEntity.User && l.ZelvoId == ticket.RequesterId, cancellationToken)
            .ConfigureAwait(false) ?? throw new ConnectorException("not-a-contact", "The requester is no longer a contact of this CRM.", true);
        var link = await db.ConnectorLinks.FirstOrDefaultAsync(l => l.ConnectorId == connector.Id && l.Entity == ConnectorEntity.Ticket && l.ZelvoId == ticket.Id, cancellationToken).ConfigureAwait(false);
        var crm = settings.Crm ?? new CrmSettings();
        var closing = ticket.State is TicketState.Resolved or TicketState.Closed && link is not { Gone: true };
        var language = await LanguageAsync(cancellationToken).ConfigureAwait(false);
        var terms = NotificationTexts.For(language).Terms;
        var note = call.Operation == "created" && link is null
            ? null
            : $"{ticket.Number}: {terms.GetValueOrDefault($"ticket.state.{ticket.State}") ?? ticket.State.ToString()}" + (ticket.ResolutionNotes is { Length: > 0 } notes && closing ? $"\n{notes}" : string.Empty);
        var page = ticket.Type == TicketType.ServiceRequest ? "requests" : "incidents";
        var id = await clients.SaveCaseAsync(connector, settings, credential, new CrmCase(
            link?.ExternalId, contact.ExternalId, ticket.Number, ticket.Title, ticket.Description, crm.StatusFor(connector.Kind, ticket.State), closing && connector.Kind == ConnectorKind.Dynamics365,
            note, new Uri(options.WebUrl, $"{language}/{page}/{ticket.Id}").AbsoluteUri), log, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            link = new ConnectorLink { TenantId = connector.TenantId, ConnectorId = connector.Id, Entity = ConnectorEntity.Ticket, ZelvoId = ticket.Id, ExternalId = id };
            db.ConnectorLinks.Add(link);
        }

        // Dataverse cases stay closed once closed: later changes add notes only.
        link.Gone = link.Gone || closing && connector.Kind == ConnectorKind.Dynamics365;
        link.SeenAt = time.GetUtcNow();
    }

    private async Task RestAsync(Connector connector, ConnectorSettings settings, string? credential, ConnectorCall call, Ticket ticket, ExchangeLog log, CancellationToken cancellationToken)
    {
        var definition = settings.Rest ?? throw new ConnectorException("not-set-up", "The connector is not fully set up.", true);
        var operation = definition.Operations.FirstOrDefault(o => o.Key == call.Operation)
            ?? throw new ConnectorException("operation-unknown", $"The connector has no operation {call.Operation}.", true);
        var outcome = await rest.RunAsync(connector, definition, credential, operation, await rest.ValuesAsync(connector.Id, ticket, cancellationToken).ConfigureAwait(false), log,
            cancellationToken).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            throw new ConnectorException(outcome.Error?.Split(':')[0] ?? "refused", outcome.Error ?? "The call failed.",
                outcome.Status is >= 400 and < 500 and not 408 and not 429 || outcome.Error?.StartsWith("path-outside-base", StringComparison.Ordinal) == true);
        }

        if (outcome.ExternalRef is { Length: > 0 } reference)
        {
            var link = await db.ConnectorLinks.FirstOrDefaultAsync(l => l.ConnectorId == connector.Id && l.Entity == ConnectorEntity.Ticket && l.ZelvoId == ticket.Id, cancellationToken).ConfigureAwait(false);
            if (link is null)
            {
                db.ConnectorLinks.Add(new ConnectorLink { TenantId = connector.TenantId, ConnectorId = connector.Id, Entity = ConnectorEntity.Ticket, ZelvoId = ticket.Id, ExternalId = reference });
            }
            else if (link.ExternalId != reference)
            {
                db.ConnectorLinks.Remove(link);
                db.ConnectorLinks.Add(new ConnectorLink { TenantId = connector.TenantId, ConnectorId = connector.Id, Entity = ConnectorEntity.Ticket, ZelvoId = ticket.Id, ExternalId = reference });
            }
        }

        // The answer's values are written as an internal note, so agents see what the other system said.
        if (outcome.Results.Count > 0)
        {
            var language = await LanguageAsync(cancellationToken).ConfigureAwait(false);
            var author = await SystemUserAsync(connector, cancellationToken).ConfigureAwait(false);
            var lines = new List<string> { operation.Name.GetOrFallback(language) ?? operation.Key };
            lines.AddRange(operation.Results.Select(r => $"{r.Name.GetOrFallback(language) ?? r.Key}: {outcome.Results.GetValueOrDefault(r.Key) ?? "—"}"));
            db.TicketComments.Add(new TicketComment
            {
                TenantId = ticket.TenantId, TicketId = ticket.Id, Visibility = CommentVisibility.Internal, Body = string.Join('\n', lines), Language = language,
                AuthorId = author, ByStaff = true, CreatedAt = time.GetUtcNow(),
            });
        }
    }

    private async Task<Guid> SystemUserAsync(Connector connector, CancellationToken cancellationToken)
    {
        if (connector.SystemUserId is { } existing)
        {
            return existing;
        }

        var account = await SystemAccounts.CreateAsync(db, "connector", connector.Name, cancellationToken).ConfigureAwait(false);
        connector.SystemUserId = account.Id;
        return account.Id;
    }

    private async Task<string> LanguageAsync(CancellationToken cancellationToken) =>
        await db.TenantSettings.AsNoTracking().Where(s => s.Key == "localization.defaultLanguage").Select(s => s.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            is "\"ar\"" ? LocalizedText.Arabic : LocalizedText.English;

    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector call {CallId} failed (attempt {Attempt}): {Code}")]
    private static partial void LogCallFailed(ILogger logger, long callId, int attempt, string code);
}

/// <summary>Runs one sync of a directory or CRM connector (scoped; tenant set) and records how it went.</summary>
public sealed class ConnectorSyncRunner(
    ZelvoDbContext db, ISecretProtector secrets, IConnectorClients clients, DirectorySync directory, CrmSync crm, TimeProvider time)
{
    /// <summary>Syncs connector <paramref name="id"/>; returns the stats, or null when it failed (the error is kept on the connector).</summary>
    public async Task<SyncStats?> RunAsync(Guid id, CancellationToken cancellationToken)
    {
        var connector = await db.Connectors.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (connector is null || !ConnectorKinds.Syncs(connector.Kind))
        {
            return null;
        }

        var started = time.GetUtcNow();
        var settings = ConnectorSettings.Read(connector.Settings);
        SyncStats? stats = null;
        string? error = null;
        var log = new ExchangeLog();
        try
        {
            var credential = await ConnectorSecrets.ReadAsync(secrets, connector, cancellationToken).ConfigureAwait(false);
            if (ConnectorKinds.IsDirectory(connector.Kind))
            {
                stats = await directory.ApplyAsync(connector, settings, await clients.ReadDirectoryAsync(connector, settings, credential, log, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var since = DateTimeOffset.TryParse(connector.SyncCursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cursor) ? cursor : (DateTimeOffset?)null;
                stats = await crm.ApplyAsync(connector, settings, await clients.ReadContactsAsync(connector, settings, credential, since, log, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                // Overlap a minute so changes made while the sync ran are read again.
                connector.SyncCursor = started.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture);
            }
        }
        catch (ConnectorException ex)
        {
            error = ex.Summary;
        }
        catch (SecretDecryptionException)
        {
            error = "key-unreadable: The stored credential cannot be decrypted. Enter it again.";
        }

        if (error is not null)
        {
            db.ChangeTracker.Clear();
            connector = await db.Connectors.FirstAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        }

        connector.LastSyncAt = started;
        connector.LastSyncSucceeded = error is null;
        connector.LastError = error is null ? null : PersonalDataMasker.MaskText(error.Length <= Connector.ErrorMaxLength ? error : error[..Connector.ErrorMaxLength]);
        connector.LastSyncStats = stats?.Write() ?? connector.LastSyncStats;
        connector.NextSyncAt = started.AddMinutes(connector.SyncMinutes);
        connector.LeaseUntil = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return stats;
    }
}

/// <summary>
/// Worker job (task E7): syncs due directory and CRM connectors and sends queued connector calls,
/// tenant by tenant, with leases (<c>FOR UPDATE SKIP LOCKED</c>) so several workers share the work,
/// and removes old calls after <see cref="ConnectorOptions.CallRetention"/>.
/// </summary>
public sealed partial class ConnectorJob(IServiceScopeFactory scopes, ConnectorOptions options, ILogger<ConnectorJob> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private DateTimeOffset _nextPurge = DateTimeOffset.MinValue;

    /// <summary>One pass over every tenant with work.</summary>
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
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT zelvo_purge_connector_calls(@before)", (NpgsqlConnection)db.Database.GetDbConnection());
            command.Parameters.AddWithValue("before", DateTimeOffset.UtcNow - options.CallRetention);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        return handled;
    }

    /// <summary>The due syncs and calls of one tenant.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        var handled = 0;
        foreach (var id in await ClaimAsync<Guid>(tenantId, slug, $"""
            UPDATE connectors SET lease_until = {DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15)}
            WHERE id IN (
                SELECT id FROM connectors
                WHERE active AND kind NOT IN ('Rest') AND next_sync_at <= now() AND (lease_until IS NULL OR lease_until < now())
                ORDER BY next_sync_at LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """, cancellationToken).ConfigureAwait(false))
        {
            await InScopeAsync(tenantId, slug, sp => sp.GetRequiredService<ConnectorSyncRunner>().RunAsync(id, cancellationToken)).ConfigureAwait(false);
            handled++;
        }

        List<long> calls;
        do
        {
            calls = await ClaimAsync<long>(tenantId, slug, $"""
                UPDATE connector_calls SET lease_until = {DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2)}
                WHERE id IN (
                    SELECT id FROM connector_calls
                    WHERE status = 'Pending' AND next_attempt_at <= now() AND (lease_until IS NULL OR lease_until < now())
                    ORDER BY next_attempt_at, id LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """, cancellationToken).ConfigureAwait(false);
            foreach (var id in calls)
            {
                await InScopeAsync(tenantId, slug, sp => sp.GetRequiredService<ConnectorCallSender>().SendAsync(id, cancellationToken)).ConfigureAwait(false);
                handled++;
            }
        }
        while (calls.Count == BatchSize);

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
                await Task.Delay(options.WorkInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task InScopeAsync(Guid tenantId, string slug, Func<IServiceProvider, Task> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "connectors", "Connectors");
        try
        {
            await work(scope.ServiceProvider).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogItemFailed(logger, slug, exception);
        }
    }

    private async Task<List<T>> ClaimAsync<T>(Guid tenantId, string slug, FormattableString sql, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        return await scope.ServiceProvider.GetRequiredService<ZelvoDbContext>().Database.SqlQuery<T>(sql).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<(Guid Id, string Slug)>> DueTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_connector_due_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Running connectors failed; trying again shortly.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A connector sync or call of tenant {Tenant} failed; it is tried again.")]
    private static partial void LogItemFailed(ILogger logger, string tenant, Exception exception);
}

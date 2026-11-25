using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Connectors;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Connectors;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Secrets;

namespace Zelvo.Api.Connectors;

/// <summary>
/// Settings → Connectors and Settings → Monitoring (module 14, task E7; product owner decisions
/// 2026-11-25): identity directories (Entra ID, AD/LDAP) synced on a schedule and offered for
/// sign-in through Keycloak; CRMs (Dynamics 365, Salesforce, HubSpot, Zoho CRM) whose contacts
/// become requesters and whose cases follow their tickets; REST connectors called from workflow
/// rules, with a test that shows the answer before saving; and monitoring sources whose alerts
/// become incidents. Credentials are encrypted and never returned; every change is audited.
/// </summary>
internal static partial class ConnectorEndpoints
{
    private static readonly JsonSerializerOptions Json = ConnectorSettings.JsonOptions;

    public static RouteGroupBuilder MapConnectorEndpoints(this RouteGroupBuilder group)
    {
        var connectors = group.MapGroup("/connectors").WithTags("Connectors").RequireTenant();
        connectors.MapGet("/", ListAsync)
            .WithName("ListConnectors")
            .WithSummary("The tenant's identity, CRM and REST connectors with their last sync. Credentials are never returned. Needs connectors.manage.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<IReadOnlyList<ConnectorResponse>>();
        connectors.MapGet("/{id:guid}", GetAsync)
            .WithName("GetConnector")
            .WithSummary("One connector. Needs connectors.manage.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<ConnectorResponse>();
        connectors.MapPost("/", CreateAsync)
            .WithName("CreateConnector")
            .WithSummary("Adds a connector: Entra ID or AD/LDAP (users, departments, managers and group members synced one way; optional sign-in through Keycloak), Dynamics 365, Salesforce, HubSpot or Zoho CRM (contacts and companies in, cases out), or a REST API with operations for workflow rules. The credential is stored encrypted. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<ConnectorResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        connectors.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateConnector")
            .WithSummary("Changes a connector; values left out stay. A new directory, server, address or app needs the credential again. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<ConnectorResponse>()
            .ProducesValidationProblem();
        connectors.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteConnector")
            .WithSummary("Removes a connector, its links and calls (and its sign-in). People it synced stay. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces(StatusCodes.Status204NoContent);
        connectors.MapPost("/{id:guid}/test", TestAsync)
            .WithName("TestConnector")
            .WithSummary("Connects with the stored settings and credential without changing anything. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<ConnectorTestResponse>();
        connectors.MapPost("/{id:guid}/sync", SyncNowAsync)
            .WithName("SyncConnectorNow")
            .WithSummary("Asks the worker to sync a directory or CRM now. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<ConnectorResponse>();
        connectors.MapGet("/{id:guid}/calls", CallsAsync)
            .WithName("ListConnectorCalls")
            .WithSummary("Calls the connector made for tickets (CRM cases, REST operations), newest first; bodies are in the integration log, masked. Kept 30 days. Needs connectors.manage.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<PageResult<ConnectorCallResponse>>();
        connectors.MapPost("/{id:guid}/calls/{callId:long}/resend", ResendAsync)
            .WithName("ResendConnectorCall")
            .WithSummary("Sends a failed call again. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<ConnectorCallResponse>();
        connectors.MapPost("/rest/test", TestRestAsync)
            .WithName("TestRestOperation")
            .WithSummary("The REST connector builder's test button: runs one operation of the settings in the request (saved or not) with sample values or an existing ticket's, and shows what was sent, the answer and the values read from it. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<RestOutcome>()
            .ProducesValidationProblem();
        connectors.MapGet("/rest/operations", OperationsAsync)
            .WithName("ListRestOperations")
            .WithSummary("REST connectors and their operations, for the workflow rule builder. Needs workflows.manage.")
            .RequirePermission(Permissions.WorkflowsManage)
            .Produces<IReadOnlyList<RestConnectorOperations>>();

        var sources = group.MapGroup("/monitoring/sources").WithTags("Connectors").RequireTenant();
        sources.MapGet("/", ListSourcesAsync)
            .WithName("ListMonitoringSources")
            .WithSummary("Monitoring tools that send alerts, with their webhook addresses. Needs connectors.manage.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<IReadOnlyList<MonitoringSourceResponse>>();
        sources.MapPost("/", CreateSourceAsync)
            .WithName("CreateMonitoringSource")
            .WithSummary("Adds a source: Prometheus Alertmanager, or any JSON with a field mapping (presets for Zabbix and Datadog). The token is shown once; only its hash is kept. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<MonitoringSourceCreated>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        sources.MapPatch("/{id:guid}", UpdateSourceAsync)
            .WithName("UpdateMonitoringSource")
            .WithSummary("Changes a source. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<MonitoringSourceResponse>()
            .ProducesValidationProblem();
        sources.MapPost("/{id:guid}/token", NewTokenAsync)
            .WithName("RenewMonitoringToken")
            .WithSummary("Makes a new token (shown once); the old one stops working at once. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<MonitoringSourceCreated>();
        sources.MapDelete("/{id:guid}", DeleteSourceAsync)
            .WithName("DeleteMonitoringSource")
            .WithSummary("Removes a source and its alert list; its incidents stay. Needs connectors.manage; audited.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces(StatusCodes.Status204NoContent);
        sources.MapGet("/{id:guid}/alerts", AlertsAsync)
            .WithName("ListMonitoringAlerts")
            .WithSummary("The source's alerts and their incidents, newest first. Needs connectors.manage.")
            .RequirePermission(Permissions.ConnectorsManage)
            .Produces<PageResult<MonitoringAlertResponse>>();

        group.MapPost("/hooks/monitoring/{id:guid}", ReceiveAlertsAsync).WithTags("Connectors").RequireTenant()
            .WithName("ReceiveMonitoringAlerts")
            .WithSummary("Alerts from a monitoring tool, with the source's token as Authorization: Bearer or ?token=. New alerts raise incidents, repeats add to them, cleared alerts add a note and, when the source says so, resolve them.")
            .AllowAnonymousAccess()
            .Produces<AlertOutcome>(StatusCodes.Status202Accepted);
        return group;
    }

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, IOptions<IdentityOptions> identity, ITenantContext tenant, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.Connectors.AsNoTracking().OrderBy(c => c.Kind).ThenBy(c => c.Name).ToListAsync(cancellationToken))
            .Select(c => ConnectorResponse.From(c, identity.Value, tenant.Slug!)).ToList());

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, IOptions<IdentityOptions> identity, ITenantContext tenant, CancellationToken cancellationToken) =>
        await db.Connectors.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is { } connector
            ? TypedResults.Ok(ConnectorResponse.From(connector, identity.Value, tenant.Slug!))
            : NotFound();

    internal static async Task<IResult> CreateAsync(
        ConnectorRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, ConnectorClients clients, IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        if (request.Kind is not { } kind)
        {
            return UserEndpoints.Validation("kind", "Choose what to connect.");
        }

        var connector = new Connector { Name = string.Empty, Kind = kind };
        if (await ApplyAsync(connector, request, db, clients, creating: true, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (Credential(kind, request) is { } credential)
        {
            await SetCredentialAsync(connector, credential, secrets, audit, cancellationToken);
        }
        else if (kind != ConnectorKind.Rest || ConnectorSettings.Read(connector.Settings).Rest?.Auth != RestAuth.None)
        {
            return UserEndpoints.Validation("credential", "Enter the credential.");
        }

        // CRMs sync every 15 minutes unless set otherwise (product owner decision 2026-11-25); directories hourly.
        if (request.SyncMinutes is null && ConnectorKinds.IsCrm(kind))
        {
            connector.SyncMinutes = 15;
        }

        connector.NextSyncAt = ConnectorKinds.Syncs(kind) && connector.Active ? DateTimeOffset.UtcNow : null;
        db.Connectors.Add(connector);
        return await SaveWithSignInAsync(connector, Credential(kind, request), db, keycloak, identity.Value, tenant, created: true, cancellationToken);
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, ConnectorRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, ConnectorClients clients, IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var connector = await db.Connectors.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (connector is null)
        {
            return NotFound();
        }

        if (request.Kind is { } kind && kind != connector.Kind)
        {
            return UserEndpoints.Validation("kind", "A connector's kind cannot change; add a new connector instead.");
        }

        var before = Destination(connector);
        if (await ApplyAsync(connector, request, db, clients, creating: false, cancellationToken) is { } problem)
        {
            return problem;
        }

        var credential = Credential(connector.Kind, request);
        if (!string.Equals(before, Destination(connector), StringComparison.Ordinal) && credential is null && connector.CredentialCiphertext is not null)
        {
            return UserEndpoints.Validation("credential", "A new directory, server, address or app needs the credential again.");
        }

        if (credential is not null)
        {
            await SetCredentialAsync(connector, credential, secrets, audit, cancellationToken);
        }

        clients.Forget(connector.Id);
        connector.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveWithSignInAsync(connector, credential, db, keycloak, identity.Value, tenant, created: false, cancellationToken);
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var connector = await db.Connectors.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (connector is null)
        {
            return NotFound();
        }

        var settings = ConnectorSettings.Read(connector.Settings);
        var signIn = settings.Entra?.SignIn == true || settings.Ldap?.SignIn == true;
        db.Connectors.Remove(connector);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (signIn)
        {
            try
            {
                await keycloak.ApplyDirectorySignInAsync(identity.Value.RealmFor(tenant.Slug!), connector.Kind == ConnectorKind.EntraId ? DirectorySignInKind.Entra : DirectorySignInKind.Ldap, null,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
            {
                return ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable", "The sign-in service did not accept the change. Nothing was changed.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> TestAsync(Guid id, ZelvoDbContext db, ISecretProtector secrets, IConnectorClients clients, IAuditLog audit, CancellationToken cancellationToken)
    {
        var connector = await db.Connectors.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (connector is null)
        {
            return NotFound();
        }

        string? detail = null;
        string? error = null;
        try
        {
            detail = await clients.TestAsync(connector, ConnectorSettings.Read(connector.Settings), await ConnectorSecrets.ReadAsync(secrets, connector, cancellationToken), new ExchangeLog(),
                cancellationToken);
        }
        catch (ConnectorException ex)
        {
            error = ex.Summary;
        }
        catch (SecretDecryptionException)
        {
            error = "key-unreadable: The stored credential cannot be decrypted. Enter it again.";
        }

        audit.Add(AuditCategory.Admin, "connectors.tested", "connector", connector.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal) { ["ok"] = error is null });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new ConnectorTestResponse(error is null, detail, error));
    }

    internal static async Task<IResult> SyncNowAsync(Guid id, ZelvoDbContext db, IAuditLog audit, IOptions<IdentityOptions> identity, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var connector = await db.Connectors.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (connector is null)
        {
            return NotFound();
        }

        if (!ConnectorKinds.Syncs(connector.Kind))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "not-synced", "REST connectors are called by workflow rules; they do not sync.");
        }

        connector.NextSyncAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "connectors.sync-requested", "connector", connector.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ConnectorResponse.From(connector, identity.Value, tenant.Slug!));
    }

    internal static async Task<IResult> CallsAsync(Guid id, [AsParameters] CallsQuery query, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Connectors.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var calls = db.ConnectorCalls.AsNoTracking().Where(c => c.ConnectorId == id);
        if (query.Status is { } status)
        {
            calls = calls.Where(c => c.Status == status);
        }

        var total = await calls.LongCountAsync(cancellationToken);
        var page = await calls.OrderByDescending(c => c.Id).Skip(query.Skip).Take(query.Size).ToListAsync(cancellationToken);
        var ticketIds = page.Where(c => c.TicketId is not null).Select(c => c.TicketId!.Value).Distinct().ToList();
        var numbers = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        return TypedResults.Ok(new PageResult<ConnectorCallResponse>([.. page.Select(c => ConnectorCallResponse.From(c, c.TicketId is { } t ? numbers.GetValueOrDefault(t) : null))],
            query.Number, query.Size, total));
    }

    internal static async Task<IResult> ResendAsync(Guid id, long callId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var call = await db.ConnectorCalls.SingleOrDefaultAsync(c => c.Id == callId && c.ConnectorId == id, cancellationToken);
        if (call is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "call-not-found", "The call does not exist.");
        }

        if (call.Status != ConnectorCallStatus.Failed)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "not-failed", "Only calls that failed can be sent again.");
        }

        call.Status = ConnectorCallStatus.Pending;
        call.Attempts = 0;
        call.NextAttemptAt = DateTimeOffset.UtcNow;
        call.FinishedAt = null;
        call.Error = null;
        audit.Add(AuditCategory.Admin, "connectors.call-resent", "connector", id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal) { ["callId"] = callId });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ConnectorCallResponse.From(call, null));
    }

    internal static async Task<IResult> TestRestAsync(
        RestTestRequest request, ZelvoDbContext db, ISecretProtector secrets, RestRunner runner, ConnectorClients clients, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (request.Settings is not { } settings)
        {
            return UserEndpoints.Validation("settings", "Send the connector's settings.");
        }

        var problems = settings.Problems().Concat(HostProblems(settings.BaseUrl, clients)).ToList();
        if (problems.Count > 0)
        {
            return Validation(problems);
        }

        var operation = settings.Operations.FirstOrDefault(o => o.Key == request.Operation);
        if (operation is null)
        {
            return UserEndpoints.Validation("operation", "Choose one of the operations.");
        }

        // A saved connector's credential may be used while its address and sign-in stay the same.
        var connector = new Connector { Name = "test", Kind = ConnectorKind.Rest, Settings = new ConnectorSettings { Rest = settings }.Write() };
        var credential = request.Credential;
        if (credential is null && request.ConnectorId is { } savedId && await db.Connectors.AsNoTracking().SingleOrDefaultAsync(c => c.Id == savedId, cancellationToken) is { } saved)
        {
            if (!string.Equals(Destination(saved), Destination(connector), StringComparison.Ordinal))
            {
                return UserEndpoints.Validation("credential", "A new address or sign-in needs the credential again.");
            }

            credential = await ConnectorSecrets.ReadAsync(secrets, saved, cancellationToken);
            connector = new Connector { Id = saved.Id, Name = saved.Name, Kind = ConnectorKind.Rest, Settings = connector.Settings };
        }

        Ticket? ticket = null;
        if (request.TicketNumber is { Length: > 0 } number)
        {
            ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Number == number, cancellationToken);
            if (ticket is null)
            {
                return UserEndpoints.Validation("ticketNumber", "There is no ticket with this number.");
            }
        }

        var outcome = await runner.RunAsync(connector, settings, credential, operation, await runner.ValuesAsync(connector.Id, ticket, cancellationToken), new ExchangeLog(), cancellationToken);
        audit.Add(AuditCategory.Admin, "connectors.rest-tested", "connector", connector.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = operation.Key, ["ok"] = outcome.Ok, ["status"] = outcome.Status, ["ticket"] = ticket?.Number,
        });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(outcome);
    }

    internal static async Task<IResult> OperationsAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.Connectors.AsNoTracking().Where(c => c.Kind == ConnectorKind.Rest).OrderBy(c => c.Name).ToListAsync(cancellationToken))
            .Select(c => new RestConnectorOperations(c.Id, c.Name, c.Active,
                [.. (ConnectorSettings.Read(c.Settings).Rest?.Operations ?? []).Select(o => new RestOperationSummary(o.Key, LocalizedTextDto.From(o.Name)))])).ToList());

    // Monitoring sources.

    internal static async Task<IResult> ListSourcesAsync(ZelvoDbContext db, Zelvo.Channels.ChannelOptions options, ITenantContext tenant, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.MonitoringSources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken)).Select(s => MonitoringSourceResponse.From(s, options, tenant.Slug!)).ToList());

    internal static async Task<IResult> CreateSourceAsync(
        MonitoringSourceRequest request, ZelvoDbContext db, Zelvo.Channels.ChannelOptions options, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var token = NewToken();
        var source = new MonitoringSource { Name = string.Empty, TokenHash = AlertIntake.Hash(token) };
        if (await ApplySourceAsync(source, request, db, creating: true, cancellationToken) is { } problem)
        {
            return problem;
        }

        var account = await SystemAccounts.CreateAsync(db, "monitoring", source.Name, cancellationToken);
        source.SystemUserId = account.Id;
        db.MonitoringSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/monitoring/sources/{source.Id}", new MonitoringSourceCreated(MonitoringSourceResponse.From(source, options, tenant.Slug!), token));
    }

    internal static async Task<IResult> UpdateSourceAsync(
        Guid id, MonitoringSourceRequest request, ZelvoDbContext db, Zelvo.Channels.ChannelOptions options, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var source = await db.MonitoringSources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        if (await ApplySourceAsync(source, request, db, creating: false, cancellationToken) is { } problem)
        {
            return problem;
        }

        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(MonitoringSourceResponse.From(source, options, tenant.Slug!));
    }

    internal static async Task<IResult> NewTokenAsync(Guid id, ZelvoDbContext db, IAuditLog audit, Zelvo.Channels.ChannelOptions options, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var source = await db.MonitoringSources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        var token = NewToken();
        source.TokenHash = AlertIntake.Hash(token);
        source.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "monitoring.token-renewed", "monitoring_source", source.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new MonitoringSourceCreated(MonitoringSourceResponse.From(source, options, tenant.Slug!), token));
    }

    internal static async Task<IResult> DeleteSourceAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var source = await db.MonitoringSources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        db.MonitoringSources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> AlertsAsync(Guid id, [AsParameters] PageQuery query, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.MonitoringSources.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return SourceNotFound();
        }

        var alerts = db.MonitoringAlerts.AsNoTracking().Where(a => a.SourceId == id);
        var total = await alerts.LongCountAsync(cancellationToken);
        var page = await alerts.OrderByDescending(a => a.LastSeenAt).Skip(query.Skip).Take(query.Size)
            .Join(db.Tickets.AsNoTracking(), a => a.TicketId, t => t.Id, (a, t) => new MonitoringAlertResponse(a.Id, a.Key, a.State, a.Count, a.FirstSeenAt, a.LastSeenAt, t.Id, t.Number, t.Title, t.State))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<MonitoringAlertResponse>(page, query.Number, query.Size, total));
    }

    internal static async Task<IResult> ReceiveAlertsAsync(Guid id, HttpRequest request, ZelvoDbContext db, AlertIntake intake, CancellationToken cancellationToken)
    {
        var source = await db.MonitoringSources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var given = request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Trim() : request.Query["token"].ToString();
        // The hash is compared, so a wrong token learns nothing about the right one.
        if (source is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(source.TokenHash), Convert.FromHexString(AlertIntake.Hash(given))))
        {
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, "token-invalid", "The token is not this source's.");
        }

        if (!source.Active)
        {
            return TypedResults.Accepted((string?)null, new AlertOutcome(0, 0, 0, 0));
        }

        JsonNode? payload;
        try
        {
            payload = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null)
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, "bad-request", "Send the alert as JSON.");
        }

        return TypedResults.Accepted((string?)null, await intake.ProcessAsync(source, payload, cancellationToken));
    }

    // Checks the request and applies it; returns a problem or null.
    private static async Task<IResult?> ApplyAsync(Connector connector, ConnectorRequest request, ZelvoDbContext db, ConnectorClients clients, bool creating, CancellationToken cancellationToken)
    {
        if (request.Name is not null || creating)
        {
            var name = request.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > Connector.NameMaxLength)
            {
                return UserEndpoints.Validation("name", $"Give the connector a name of up to {Connector.NameMaxLength} characters.");
            }

            connector.Name = name;
        }

        connector.Active = request.Active ?? connector.Active;
        if (request.SyncMinutes is { } minutes)
        {
            if (minutes is < 5 or > 10080)
            {
                return UserEndpoints.Validation("syncMinutes", "Sync every 5 minutes to 7 days (10,080 minutes).");
            }

            connector.SyncMinutes = minutes;
        }

        var current = ConnectorSettings.Read(connector.Settings);
        var next = current;
        var problems = new List<(string Field, string Message)>();
        switch (connector.Kind)
        {
            case ConnectorKind.EntraId:
                var entra = request.Entra ?? current.Entra;
                if (entra is null)
                {
                    problems.Add(("entra", "Set up the directory."));
                    break;
                }

                if (!(Guid.TryParse(entra.DirectoryId, out _) || Domain().IsMatch(entra.DirectoryId)) || !Guid.TryParse(entra.ClientId, out _))
                {
                    problems.Add(("entra.directoryId", "Enter the directory (tenant) id or domain and the app's client id."));
                }

                if (entra.SignIn && !Guid.TryParse(entra.DirectoryId, out _))
                {
                    problems.Add(("entra.signIn", "Sign-in with Microsoft needs the directory (tenant) id, not its domain."));
                }

                if (entra.UsersGroupId is { Length: > 0 } usersGroup && !Guid.TryParse(usersGroup, out _))
                {
                    problems.Add(("entra.usersGroupId", "Enter the group's object id."));
                }

                problems.AddRange(await MappingProblemsAsync(db, entra.GroupMappings, "entra.groupMappings", id => Guid.TryParse(id, out _), cancellationToken));
                if (entra.SignIn && (await db.Connectors.AsNoTracking().Where(c => c.Id != connector.Id && c.Kind == ConnectorKind.EntraId).Select(c => c.Settings).ToListAsync(cancellationToken))
                    .Any(s => ConnectorSettings.Read(s).Entra?.SignIn == true))
                {
                    problems.Add(("entra.signIn", "Another Entra connector already offers sign-in."));
                }

                next = current with { Entra = entra };
                break;
            case ConnectorKind.Ldap:
                var ldap = request.Ldap ?? current.Ldap;
                if (ldap is null)
                {
                    problems.Add(("ldap", "Set up the directory."));
                    break;
                }

                if (Uri.CheckHostName(ldap.Host) is UriHostNameType.Unknown or UriHostNameType.Basic || ldap.Port is < 1 or > 65535
                    || IPAddress.TryParse(ldap.Host, out var literal) && !clients.Policy.IsAllowed(literal))
                {
                    problems.Add(("ldap.host", "Enter the directory server's host name and port. The operator can allow internal networks (Connectors:Network:AllowedPrivateNetworks)."));
                }

                if (string.IsNullOrWhiteSpace(ldap.BindDn) || string.IsNullOrWhiteSpace(ldap.BaseDn) || !ldap.UserFilter.StartsWith('(') || !ldap.UserFilter.EndsWith(')'))
                {
                    problems.Add(("ldap.baseDn", "Enter the service account, where people are and a filter in brackets."));
                }

                problems.AddRange(await MappingProblemsAsync(db, ldap.GroupMappings, "ldap.groupMappings", id => id.Contains('=', StringComparison.Ordinal), cancellationToken));
                if (ldap.SignIn && (await db.Connectors.AsNoTracking().Where(c => c.Id != connector.Id && c.Kind == ConnectorKind.Ldap).Select(c => c.Settings).ToListAsync(cancellationToken))
                    .Any(s => ConnectorSettings.Read(s).Ldap?.SignIn == true))
                {
                    problems.Add(("ldap.signIn", "Another directory connector already offers sign-in."));
                }

                next = current with { Ldap = ldap };
                break;
            case ConnectorKind.Rest:
                var rest = request.Rest ?? current.Rest;
                if (rest is null)
                {
                    problems.Add(("rest", "Set up the API and its operations."));
                    break;
                }

                problems.AddRange(rest.Problems());
                problems.AddRange(HostProblems(rest.BaseUrl, clients));
                next = current with { Rest = rest };
                break;
            default:
                var crm = request.Crm ?? current.Crm ?? new CrmSettings();
                var needsUrl = connector.Kind is ConnectorKind.Dynamics365 or ConnectorKind.Salesforce or ConnectorKind.Zoho;
                if (needsUrl && (!Uri.TryCreate(crm.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps))
                {
                    problems.Add(("crm.url", "Enter the CRM's address, starting with https://."));
                }
                else if (needsUrl)
                {
                    problems.AddRange(HostProblems(crm.Url!, clients).Select(p => ("crm.url", p.Message)));
                }

                if (connector.Kind == ConnectorKind.Zoho && (!Uri.TryCreate(crm.ApiUrl, UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps))
                {
                    problems.Add(("crm.apiUrl", "Enter Zoho's API domain of your data centre, starting with https://."));
                }

                if (connector.Kind == ConnectorKind.Dynamics365 && !Guid.TryParse(crm.DirectoryId, out _))
                {
                    problems.Add(("crm.directoryId", "Enter the Entra directory (tenant) id."));
                }

                if (connector.Kind != ConnectorKind.HubSpot && string.IsNullOrWhiteSpace(crm.ClientId))
                {
                    problems.Add(("crm.clientId", "Enter the app's client id."));
                }

                if (crm.StatusMap.Any(m => !Enum.TryParse<TicketState>(m.Key, out _) || string.IsNullOrWhiteSpace(m.Value) || m.Value.Length > 100))
                {
                    problems.Add(("crm.statusMap", "Map ticket states (New, Assigned, InProgress, OnHold, Resolved, Closed, Reopened) to the CRM's statuses."));
                }

                next = current with { Crm = crm };
                break;
        }

        if (problems.Count > 0)
        {
            return Validation(problems);
        }

        connector.Settings = next.Write();
        return null;
    }

    private static async Task<IResult> SaveWithSignInAsync(
        Connector connector, string? credential, ZelvoDbContext db, IKeycloakAdmin keycloak, IdentityOptions identity, ITenantContext tenant, bool created, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (ConnectorKinds.IsDirectory(connector.Kind))
        {
            var settings = ConnectorSettings.Read(connector.Settings);
            var kind = connector.Kind == ConnectorKind.EntraId ? DirectorySignInKind.Entra : DirectorySignInKind.Ldap;
            var wanted = connector.Active && (settings.Entra?.SignIn == true || settings.Ldap?.SignIn == true);
            // Nothing goes to Keycloak for directories that never offered sign-in.
            if (wanted || !created)
            {
                try
                {
                    await keycloak.ApplyDirectorySignInAsync(identity.RealmFor(tenant.Slug!), kind, wanted ? SignInOf(settings, credential, connector.Name) : null, cancellationToken);
                }
                catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
                {
                    return ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable", "The sign-in service did not accept the change. Nothing was changed.");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        var response = ConnectorResponse.From(connector, identity, tenant.Slug!);
        return created ? TypedResults.Created($"/api/v1/connectors/{connector.Id}", response) : TypedResults.Ok(response);
    }

    private static DirectorySignIn SignInOf(ConnectorSettings settings, string? secret, string name) =>
        settings.Entra is { } entra
            ? new DirectorySignIn(entra.DirectoryId, entra.ClientId, secret, null, 0, false, false, null, null, null, false, name)
            : new DirectorySignIn(null, null, secret, settings.Ldap!.Host, settings.Ldap.Port, settings.Ldap.Security == LdapSecurity.Ldaps, settings.Ldap.Security == LdapSecurity.StartTls,
                settings.Ldap.BindDn, settings.Ldap.BaseDn, settings.Ldap.UserFilter, settings.Ldap.ActiveDirectory, name);

    private static async Task<List<(string Field, string Message)>> MappingProblemsAsync(
        ZelvoDbContext db, IReadOnlyList<GroupMapping> mappings, string field, Func<string, bool> validId, CancellationToken cancellationToken)
    {
        var problems = new List<(string, string)>();
        if (mappings.Count > 50 || mappings.Any(m => !validId(m.ExternalId)))
        {
            problems.Add((field, "Name up to 50 directory groups by their id."));
        }

        var groups = mappings.Select(m => m.GroupId).Distinct().ToList();
        if (await db.SupportGroups.CountAsync(g => groups.Contains(g.Id), cancellationToken) != groups.Count)
        {
            problems.Add((field, "Choose existing Zelvo groups."));
        }

        return problems;
    }

    private static IEnumerable<(string Field, string Message)> HostProblems(string url, ConnectorClients clients)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && clients.Policy.CheckUrl(url, out _) is { } code)
        {
            yield return ("rest.baseUrl", code == "url-not-https"
                ? "Connected systems are reached over HTTPS."
                : $"Zelvo may not connect to {parsed.Host}. The operator can allow internal networks (Connectors:Network:AllowedPrivateNetworks).");
        }
    }

    private static async Task<IResult?> ApplySourceAsync(MonitoringSource source, MonitoringSourceRequest request, ZelvoDbContext db, bool creating, CancellationToken cancellationToken)
    {
        if (request.Name is not null || creating)
        {
            var name = request.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > MonitoringSource.NameMaxLength)
            {
                return UserEndpoints.Validation("name", $"Give the source a name of up to {MonitoringSource.NameMaxLength} characters.");
            }

            source.Name = name;
        }

        source.Format = request.Format ?? source.Format;
        var mapping = request.Preset switch
        {
            "zabbix" => AlertMapping.Zabbix,
            "datadog" => AlertMapping.Datadog,
            _ => request.Mapping,
        };
        if (mapping is not null)
        {
            var problems = mapping.Problems().ToList();
            if (problems.Count > 0)
            {
                return Validation(problems);
            }

            source.Mapping = mapping.Write();
        }
        else if (creating && source.Format == AlertFormat.Generic)
        {
            return UserEndpoints.Validation("mapping", "Choose a preset or map the alert's fields.");
        }

        source.ResolveOnClear = request.ResolveOnClear ?? source.ResolveOnClear;
        source.Active = request.Active ?? source.Active;
        if (request.CategoryId is not null || request.GroupId is not null || request.LocationId is not null || creating)
        {
            source.CategoryId = request.CategoryId == Guid.Empty ? null : request.CategoryId ?? source.CategoryId;
            source.GroupId = request.GroupId == Guid.Empty ? null : request.GroupId ?? source.GroupId;
            source.LocationId = request.LocationId == Guid.Empty ? null : request.LocationId ?? source.LocationId;
            if (source.CategoryId is { } category && !await db.Categories.AnyAsync(c => c.Id == category, cancellationToken))
            {
                return UserEndpoints.Validation("categoryId", "Choose one of the tenant's categories.");
            }

            if (source.GroupId is { } group && !await db.SupportGroups.AnyAsync(g => g.Id == group, cancellationToken))
            {
                return UserEndpoints.Validation("groupId", "Choose one of the tenant's groups.");
            }

            if (source.LocationId is { } location && !await db.Locations.AnyAsync(l => l.Id == location, cancellationToken))
            {
                return UserEndpoints.Validation("locationId", "Choose one of the tenant's locations.");
            }
        }

        return null;
    }

    private static string? Credential(ConnectorKind kind, ConnectorRequest request) =>
        request.Credential is not { Length: > 0 } credential
            ? null
            : kind == ConnectorKind.Zoho
                ? new ZohoCredential(credential, request.RefreshToken ?? string.Empty).Write()
                : credential;

    private static async Task SetCredentialAsync(Connector connector, string credential, ISecretProtector secrets, IAuditLog audit, CancellationToken cancellationToken)
    {
        connector.CredentialCiphertext = await secrets.ProtectAsync(ConnectorSecrets.Purpose(connector.Id), credential, cancellationToken);
        connector.CredentialSetAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "connectors.credential-changed", "connector", connector.Id.ToString());
    }

    // What the credential was given for: a change needs it again.
    private static string Destination(Connector connector)
    {
        var s = ConnectorSettings.Read(connector.Settings);
        return connector.Kind switch
        {
            ConnectorKind.EntraId => $"{s.Entra?.DirectoryId}|{s.Entra?.ClientId}",
            ConnectorKind.Ldap => $"{s.Ldap?.Host}|{s.Ldap?.Port}|{s.Ldap?.BindDn}|{s.Ldap?.Security}",
            ConnectorKind.Rest => $"{(Uri.TryCreate(s.Rest?.BaseUrl, UriKind.Absolute, out var b) ? b.Authority : null)}|{s.Rest?.Auth}|{s.Rest?.TokenUrl}|{s.Rest?.ClientId}|{s.Rest?.AuthHeader}|{s.Rest?.Username}",
            _ => $"{s.Crm?.Url}|{s.Crm?.ApiUrl}|{s.Crm?.DirectoryId}|{s.Crm?.ClientId}",
        };
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem Validation(IEnumerable<(string Field, string Message)> problems) =>
        TypedResults.ValidationProblem(problems.GroupBy(p => p.Field).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray(), StringComparer.Ordinal));

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "connector-not-found", "The connector does not exist.");

    private static IResult SourceNotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "source-not-found", "The monitoring source does not exist.");

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex Domain();
}

/// <summary>Adds or changes a connector; values left out stay.</summary>
public sealed class ConnectorRequest
{
    /// <summary>Name shown to administrators.</summary>
    public string? Name { get; init; }

    /// <summary>EntraId, Ldap, Dynamics365, Salesforce, HubSpot, Zoho or Rest (fixed once added).</summary>
    public ConnectorKind? Kind { get; init; }

    /// <summary>Entra ID settings.</summary>
    public EntraSettings? Entra { get; init; }

    /// <summary>LDAP settings.</summary>
    public LdapSettings? Ldap { get; init; }

    /// <summary>CRM settings.</summary>
    public CrmSettings? Crm { get; init; }

    /// <summary>REST settings.</summary>
    public RestSettings? Rest { get; init; }

    /// <summary>Client secret, bind password, access token or API key; never returned.</summary>
    public string? Credential { get; init; }

    /// <summary>Zoho: the refresh token of the administrator's grant.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>Whether it syncs and can be called.</summary>
    public bool? Active { get; init; }

    /// <summary>Minutes between syncs (5 to 10,080).</summary>
    public int? SyncMinutes { get; init; }
}

/// <summary>The REST builder's test.</summary>
public sealed class RestTestRequest
{
    /// <summary>The saved connector whose credential to use (when no credential is sent).</summary>
    public Guid? ConnectorId { get; init; }

    /// <summary>The settings to test (saved or not).</summary>
    public RestSettings? Settings { get; init; }

    /// <summary>A credential to test with.</summary>
    public string? Credential { get; init; }

    /// <summary>The operation's key.</summary>
    [Required]
    public string? Operation { get; init; }

    /// <summary>A ticket whose values to use; sample values otherwise.</summary>
    public string? TicketNumber { get; init; }
}

/// <summary>Adds or changes a monitoring source; values left out stay.</summary>
public sealed class MonitoringSourceRequest
{
    /// <summary>Name, also the incidents' requester.</summary>
    public string? Name { get; init; }

    /// <summary>Alertmanager or Generic.</summary>
    public AlertFormat? Format { get; init; }

    /// <summary>Generic: <c>zabbix</c> or <c>datadog</c> fills in the mapping.</summary>
    public string? Preset { get; init; }

    /// <summary>Generic: the field mapping.</summary>
    public AlertMapping? Mapping { get; init; }

    /// <summary>Category of the incidents (empty id: none).</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Location of the incidents (empty id: none).</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Group of the incidents (empty id: the assignment rules).</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Resolve incidents when their alert clears.</summary>
    public bool? ResolveOnClear { get; init; }

    /// <summary>Whether alerts are taken.</summary>
    public bool? Active { get; init; }
}

/// <summary>Filters of the call log.</summary>
internal sealed class CallsQuery
{
    /// <summary>Status.</summary>
    public ConnectorCallStatus? Status { get; init; }

    /// <summary>Page, from 1.</summary>
    [Range(1, 100_000)]
    public int? Page { get; init; }

    /// <summary>Page size.</summary>
    [Range(1, PageQuery.MaxPageSize)]
    public int? PageSize { get; init; }

    internal int Number => Page ?? 1;

    internal int Size => PageSize ?? PageQuery.DefaultPageSize;

    internal int Skip => (Number - 1) * Size;
}

/// <summary>A connector as administrators see it (never its credential).</summary>
internal sealed record ConnectorResponse(
    Guid Id, string Name, ConnectorKind Kind, JsonNode Settings, bool CredentialSet, DateTimeOffset? CredentialSetAt, bool Active, int SyncMinutes,
    DateTimeOffset? LastSyncAt, bool? LastSyncSucceeded, JsonNode? LastSyncStats, DateTimeOffset? NextSyncAt, string? LastError, string? SignInRedirectUri,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static ConnectorResponse From(Connector c, IdentityOptions identity, string tenantSlug) => new(
        c.Id, c.Name, c.Kind, JsonNode.Parse(c.Settings) ?? new JsonObject(), c.CredentialCiphertext is not null, c.CredentialSetAt, c.Active, c.SyncMinutes,
        c.LastSyncAt, c.LastSyncSucceeded, c.LastSyncStats is { } stats ? JsonNode.Parse(stats) : null, ConnectorKinds.Syncs(c.Kind) ? c.NextSyncAt : null, c.LastError,
        // Register this redirect address with the Entra app for "Sign in with Microsoft".
        c.Kind == ConnectorKind.EntraId ? $"{identity.PublicUrl.TrimEnd('/')}/realms/{identity.RealmFor(tenantSlug)}/broker/{KeycloakAdminClient.EntraAlias}/endpoint" : null,
        c.CreatedAt, c.UpdatedAt);
}

/// <summary>What a connector test found.</summary>
internal sealed record ConnectorTestResponse(bool Ok, string? Detail, string? Error);

/// <summary>A connector call.</summary>
internal sealed record ConnectorCallResponse(
    long Id, ConnectorCallKind Kind, string Operation, Guid? TicketId, string? TicketNumber, Guid? RuleId, ConnectorCallStatus Status, int Attempts, int? StatusCode, int? DurationMs,
    string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt)
{
    public static ConnectorCallResponse From(ConnectorCall c, string? number) =>
        new(c.Id, c.Kind, c.Operation, c.TicketId, number, c.RuleId, c.Status, c.Attempts, c.StatusCode, c.DurationMs, c.Error, c.CreatedAt, c.FinishedAt);
}

/// <summary>A REST connector's operations for the rule builder.</summary>
internal sealed record RestConnectorOperations(Guid Id, string Name, bool Active, IReadOnlyList<RestOperationSummary> Operations);

/// <summary>One operation.</summary>
internal sealed record RestOperationSummary(string Key, LocalizedTextDto Name);

/// <summary>A monitoring source (never its token).</summary>
internal sealed record MonitoringSourceResponse(
    Guid Id, string Name, AlertFormat Format, JsonNode Mapping, string WebhookUrl, Guid? CategoryId, Guid? LocationId, Guid? GroupId, bool ResolveOnClear, bool Active,
    DateTimeOffset? LastAlertAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static MonitoringSourceResponse From(MonitoringSource s, Zelvo.Channels.ChannelOptions options, string tenantSlug) => new(
        s.Id, s.Name, s.Format, JsonNode.Parse(s.Mapping) ?? new JsonObject(), new Uri(options.PublicUrl, $"hooks/{tenantSlug}/monitoring/{s.Id}").AbsoluteUri,
        s.CategoryId, s.LocationId, s.GroupId, s.ResolveOnClear, s.Active, s.LastAlertAt, s.CreatedAt, s.UpdatedAt);
}

/// <summary>A source with its token, shown once.</summary>
internal sealed record MonitoringSourceCreated(MonitoringSourceResponse Source, string Token);

/// <summary>An alert and its incident.</summary>
internal sealed record MonitoringAlertResponse(
    long Id, string Key, AlertState State, int Count, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, Guid TicketId, string TicketNumber, string TicketTitle, TicketState TicketState);

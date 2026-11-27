using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Integrations;
using Zelvo.Search;

namespace Zelvo.Tickets;

/// <summary>Finds tenants with work for a job through a <c>SECURITY DEFINER</c> function that returns only ids and slugs.</summary>
internal static class TenantsWithWork
{
    public static async Task<List<(Guid Id, string Slug)>> ListAsync(IServiceScopeFactory scopes, string function, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        // The function name is one of this assembly's constants, never input.
        await using var command = new NpgsqlCommand($"SELECT id, slug FROM {function}()", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }
}

/// <summary>
/// Worker job: closes tickets that stayed resolved for the tenant's auto-close period (product owner
/// decision 2026-11-22: a tenant setting, 5 calendar days by default). Changes are audited with
/// the system as actor, and the search index follows.
/// </summary>
public sealed partial class TicketAutoCloseJob(IServiceScopeFactory scopes, ILogger<TicketAutoCloseJob> logger, TimeProvider time) : BackgroundService
{
    private const int BatchSize = 200;

    /// <summary>How often the job runs.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Closes everything due now; returns how many tickets were closed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var (tenantId, slug) in await TenantsWithWork.ListAsync(scopes, "zelvo_tenants_with_resolved_tickets", cancellationToken).ConfigureAwait(false))
        {
            closed += await CloseTenantAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
        }

        return closed;
    }

    /// <summary>Closes one tenant's tickets that are due.</summary>
    internal async Task<int> CloseTenantAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        var closed = 0;
        int count;
        do
        {
            count = await CloseBatchAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
            closed += count;
        }
        while (count == BatchSize);
        return closed;
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
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> CloseBatchAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "tickets.auto-close", "Auto-close");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var indexer = scope.ServiceProvider.GetRequiredService<ISearchIndexer>();

        var days = await TicketSettingKeys.ReadAutoCloseDaysAsync(db, cancellationToken).ConfigureAwait(false);
        var businessDays = await TicketSettingKeys.ReadAutoCloseBusinessDaysAsync(db, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        // Working days are never shorter than calendar days, so this finds every candidate.
        var due = now.AddDays(-days);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        // xmin is the row version (a system column that SELECT * leaves out).
        var tickets = await db.Tickets
            .FromSql($"""
                SELECT *, xmin FROM tickets
                WHERE state = 'Resolved' AND resolved_at < {due}
                  -- Task E8: a request whose fee is due on completion closes only once paid.
                  AND NOT EXISTS (SELECT 1 FROM payments p WHERE p.ticket_id = tickets.id AND p.state = 'AwaitingPayment')
                  -- Task C5: a bundle closes when its parts are closed.
                  AND NOT EXISTS (SELECT 1 FROM service_requests r WHERE r.ticket_id = tickets.id AND r.is_bundle)
                ORDER BY resolved_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var selected = tickets.Count;
        if (businessDays && tickets.Count > 0)
        {
            // Task B3: working days of the tenant's default calendar. Due times grow with the
            // resolution time, so the tickets due form the start of the list.
            var setup = await Sla.SlaSetup.LoadAsync(db, cancellationToken).ConfigureAwait(false);
            var calendar = setup.Calendar(setup.DefaultCalendarId);
            tickets = [.. tickets.TakeWhile(t => calendar.AddWorkingDays(t.ResolvedAt!.Value, days) <= now)];
        }

        if (tickets.Count == 0)
        {
            return 0;
        }

        var ids = tickets.Select(t => t.Id).ToList();
        await db.Incidents.Where(i => ids.Contains(i.TicketId)).LoadAsync(cancellationToken).ConfigureAwait(false);
        await db.CustomerVoiceRecords.Where(v => ids.Contains(v.TicketId)).LoadAsync(cancellationToken).ConfigureAwait(false);
        await db.Problems.Where(p => ids.Contains(p.TicketId)).LoadAsync(cancellationToken).ConfigureAwait(false);
        await db.CustomRecords.Where(r => ids.Contains(r.TicketId)).LoadAsync(cancellationToken).ConfigureAwait(false);

        var staff = await IncidentSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string>? voiceStaff = null;
        IReadOnlyList<string>? requestStaff = null;
        IReadOnlyList<string>? problemStaff = null;
        foreach (var ticket in tickets)
        {
            TicketWorkflow.Apply(ticket, TicketState.Closed, null, null, now);
            if (ticket.Type == TicketType.Incident)
            {
                indexer.Upsert(IncidentSearch.Document(ticket, staff));
            }
            else if (ticket.Type == TicketType.ServiceRequest)
            {
                requestStaff ??= await RequestSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
                indexer.Upsert(RequestSearch.Document(ticket, requestStaff));
            }
            else if (ticket.Type == TicketType.Custom)
            {
                await RecordSearch.IndexAsync(db, indexer, [ticket], cancellationToken).ConfigureAwait(false);
            }
            else if (ticket.Type == TicketType.Problem)
            {
                problemStaff ??= await ProblemSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
                indexer.Upsert(ProblemSearch.Document(ticket, problemStaff));
            }
            else if (ticket.Voice is { } voice)
            {
                // Task C6 (product owner decision 2026-11-24): a resolved complaint the customer did
                // not answer closes after the period and is recorded as not confirmed.
                if (voice.Confirmation == Data.Voice.ComplaintConfirmation.Pending)
                {
                    voice.Confirmation = Data.Voice.ComplaintConfirmation.NotConfirmed;
                    voice.ConfirmationAt = now;
                }

                voiceStaff ??= await VoiceSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
                indexer.Upsert(VoiceSearch.Document(ticket, voiceStaff));
            }
        }

        await scope.ServiceProvider.GetRequiredService<Sla.SlaTracker>().UpdateAsync(tickets, now, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogClosed(logger, tickets.Count, slug);
        // A full batch keeps the caller going; a short one (business days) means the rest is not due.
        return tickets.Count == selected ? tickets.Count : Math.Min(tickets.Count, BatchSize - 1);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Closing resolved tickets failed; trying again shortly.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed {Count} resolved tickets of tenant {Tenant}.")]
    private static partial void LogClosed(ILogger logger, int count, string tenant);
}

/// <summary>
/// Worker job: scans waiting attachments for malware (product owner decision 2026-11-22). Clean
/// files become downloadable; infected files are rejected, their content deleted and the
/// rejection audited. When the scanner cannot answer, the file stays unavailable and is tried again.
/// </summary>
public sealed partial class AttachmentScanJob(
    IServiceScopeFactory scopes, IMalwareScanner scanner, IOptions<AttachmentScanningOptions> options,
    ILogger<AttachmentScanJob> logger, TimeProvider time) : BackgroundService
{
    private const int BatchSize = 10;

    /// <summary>Scans everything waiting now; returns how many files got a verdict.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var scanned = 0;
        foreach (var (tenantId, slug) in await TenantsWithWork.ListAsync(scopes, "zelvo_tenants_with_pending_scans", cancellationToken).ConfigureAwait(false))
        {
            scanned += await ScanBatchAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
        }

        return scanned;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.Value.PollInterval;
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (MalwareScannerException exception)
            {
                LogScannerUnavailable(logger, exception);
                delay = TimeSpan.FromSeconds(15);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
                delay = TimeSpan.FromSeconds(15);
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Scans one tenant's waiting files (one batch).</summary>
    internal Task<int> ScanTenantAsync(Guid tenantId, string slug, CancellationToken cancellationToken) =>
        ScanBatchAsync(tenantId, slug, cancellationToken);

    private async Task<int> ScanBatchAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "attachments.malware-scan", "Malware scan");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var attachments = await db.TicketAttachments
            .FromSql($"""
                SELECT * FROM ticket_attachments
                WHERE scan_status = 'Pending'
                ORDER BY uploaded_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var verdicts = 0;
        MalwareScannerException? unavailable = null;
        foreach (var attachment in attachments)
        {
            var content = await store.ReadAsync(attachment.Id, cancellationToken).ConfigureAwait(false);
            ScanVerdict verdict;
            try
            {
                verdict = content is null ? new ScanVerdict(false, "content-missing") : await scanner.ScanAsync(content, cancellationToken).ConfigureAwait(false);
            }
            catch (MalwareScannerException exception)
            {
                attachment.ScanAttempts++;
                attachment.ScanResult = exception.Message.Length > TicketAttachment.ScanResultMaxLength
                    ? exception.Message[..TicketAttachment.ScanResultMaxLength] : exception.Message;
                unavailable = exception;
                break;
            }

            attachment.ScanAttempts++;
            attachment.ScannedAt = time.GetUtcNow();
            attachment.ScanResult = verdict.Signature;
            if (verdict.Clean)
            {
                attachment.ScanStatus = AttachmentScanStatus.Clean;
            }
            else
            {
                attachment.ScanStatus = AttachmentScanStatus.Infected;
                await store.RemoveAsync(attachment.Id, cancellationToken).ConfigureAwait(false);
                // Task C1: files uploaded for a form that was not sent yet belong to no ticket.
                var (action, entityType, entityId) = (attachment.TicketId, attachment.ArticleId, attachment.ServiceId) switch
                {
                    ({ } ticketId, _, _) => ("tickets.attachment.infected", "ticket", ticketId.ToString()),
                    (_, { } articleId, _) => ("knowledge.image.infected", "kb_article", articleId.ToString()),
                    // Task C2: a service's image or document template.
                    (_, _, { } serviceId) => ("catalog.file.infected", "catalog_service", serviceId.ToString()),
                    _ => ("forms.upload.infected", "ticket_attachment", attachment.Id.ToString()),
                };
                audit.Add(AuditCategory.Security, action, entityType, entityId,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["attachmentId"] = attachment.Id.ToString(),
                        ["fileName"] = attachment.FileName,
                        ["sha256"] = attachment.Sha256,
                        ["signature"] = verdict.Signature,
                    });
                LogInfected(logger, attachment.Id, slug);
            }

            verdicts++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            throw unavailable;
        }

        return verdicts;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The malware scanner is not available; attachments stay unavailable until it is.")]
    private static partial void LogScannerUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scanning attachments failed; trying again shortly.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malware found in attachment {AttachmentId} of tenant {Tenant}; the file was rejected.")]
    private static partial void LogInfected(ILogger logger, Guid attachmentId, string tenant);
}

/// <summary>Registers tickets for the API and the worker.</summary>
public static class TicketServiceCollectionExtensions
{
    /// <summary>What both hosts need: the attachment store, the scanner and its options. Needs <c>AddZelvoData</c> and <c>AddZelvoSearch</c>.</summary>
    public static IServiceCollection AddZelvoTickets(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<AttachmentScanningOptions>()
            .Configure(o =>
            {
                var section = configuration.GetSection(AttachmentScanningOptions.Section);
                o.Enabled = ScanningEnabled(configuration);
                o.Host = section["Host"] is { Length: > 0 } host ? host : o.Host;
                o.Port = int.TryParse(section["Port"], System.Globalization.CultureInfo.InvariantCulture, out var port) ? port : o.Port;
                o.Timeout = TimeSpan.TryParse(section["Timeout"], System.Globalization.CultureInfo.InvariantCulture, out var timeout) ? timeout : o.Timeout;
                o.PollInterval = TimeSpan.TryParse(section["PollInterval"], System.Globalization.CultureInfo.InvariantCulture, out var poll) ? poll : o.PollInterval;
            })
            .Validate(o => o.Port is > 0 and < 65536 && !string.IsNullOrWhiteSpace(o.Host), "AttachmentScanning:Host and Port are required.");
        services.TryAddSingleton(TimeProvider.System);
        // Attachment content in PostgreSQL, or in S3-compatible object storage (task F6).
        var storage = AttachmentStorageOptions.From(configuration);
        services.TryAddSingleton(storage);
        if (storage.UsesObjectStorage)
        {
            services.TryAddSingleton<IObjectStorage>(_ => new S3ObjectStorage(storage));
            services.TryAddScoped<PendingObjectUploads>();
            services.AddZelvoSaveChangesInterceptor<ObjectUploadInterceptor>();
            services.TryAddScoped<IAttachmentStore, ObjectAttachmentStore>();
        }

        services.TryAddScoped<IAttachmentStore, PostgresAttachmentStore>();
        services.TryAddSingleton<IMalwareScanner, ClamAvScanner>();
        // Task M1: ClamAV's definitions (the private mirror, what ClamAV has loaded).
        services.TryAddSingleton<Malware.MalwareDefinitionStore>();
        services.TryAddSingleton<Malware.IClamAvVersionClient, Malware.ClamAvVersionClient>();
        services.AddScoped<ISearchDocumentSource, IncidentSearchSource>();
        services.AddScoped<ISearchDocumentSource, VoiceSearchSource>();
        services.AddScoped<ISearchDocumentSource, RequestSearchSource>();
        services.AddScoped<ISearchDocumentSource, ProblemSearchSource>();
        services.AddScoped<ISearchDocumentSource, KnownErrorSearchSource>();
        // Task K2: custom records' search documents and their states following the ticket core.
        services.AddScoped<ISearchDocumentSource, RecordSearchSource>();
        services.AddZelvoSaveChangesInterceptor<CustomRecordStateInterceptor>();
        services.TryAddScoped<Sla.SlaTracker>();
        services.TryAddScoped<IncidentIntake>();
        if (!services.Any(d => d.ImplementationType == typeof(Sla.SlaWebhookEvents)))
        {
            services.AddWebhookEventSource<Sla.SlaWebhookEvents>();
        }

        services.AddSearchType(IncidentSearch.Type, null);
        services.AddSearchType(VoiceSearch.Type, null);
        services.AddSearchType(RequestSearch.Type, null);
        services.AddSearchType(ProblemSearch.Type, ProblemPermissions.Read);
        services.AddSearchType(ProblemSearch.KnownErrorType, ProblemPermissions.Read);
        services.AddSearchType(RecordSearch.Type, null);
        // Task F1: DNS lookups of the guided domain checks.
        services.TryAddSingleton<Branding.IDnsLookup, Branding.SystemDnsLookup>();
        return services;
    }

    /// <summary>The worker jobs: auto-close, and malware scanning unless it is turned off.</summary>
    public static IServiceCollection AddZelvoTicketJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoTickets(configuration);
        if (AttachmentStorageOptions.From(configuration).UsesObjectStorage)
        {
            services.AddHostedService<ObjectStorageSweeper>();
        }

        services.TryAddSingleton<TicketAutoCloseJob>();
        services.AddHostedService(sp => sp.GetRequiredService<TicketAutoCloseJob>());
        services.TryAddSingleton<Sla.SlaTimerJob>();
        services.AddHostedService(sp => sp.GetRequiredService<Sla.SlaTimerJob>());
        services.TryAddSingleton<Forms.FormUploadCleanupJob>();
        services.AddHostedService(sp => sp.GetRequiredService<Forms.FormUploadCleanupJob>());
        // Task F1: branded PDF exports with headless Chromium.
        services.TryAddSingleton(_ => Exports.PdfOptions.From(configuration));
        services.TryAddSingleton<Exports.IPdfRenderer, Exports.ChromiumPdfRenderer>();
        services.TryAddSingleton<Exports.DocumentExportJob>();
        services.AddHostedService(sp => sp.GetRequiredService<Exports.DocumentExportJob>());
        if (ScanningEnabled(configuration))
        {
            services.TryAddSingleton<AttachmentScanJob>();
            services.AddHostedService(sp => sp.GetRequiredService<AttachmentScanJob>());
            services.TryAddSingleton<Malware.ClamAvStatusJob>();
            services.AddHostedService(sp => sp.GetRequiredService<Malware.ClamAvStatusJob>());
        }

        return services;
    }

    /// <summary>Scanning is on unless <c>AttachmentScanning:Enabled</c> is <c>false</c>.</summary>
    public static bool ScanningEnabled(IConfiguration configuration) =>
        !bool.TryParse(configuration?[$"{AttachmentScanningOptions.Section}:Enabled"], out var enabled) || enabled;
}

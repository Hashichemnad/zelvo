using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Reports;
using Zelvo.Data.Tenancy;
using Zelvo.Tickets.Exports;
using Zelvo.Tickets.Reports;

namespace Zelvo.Notifications;

/// <summary>
/// Worker job of scheduled reports (module 10, task F2): queues one email per recipient when a
/// schedule is due, then builds each report with that recipient's own access (people who lost
/// access or left are skipped), prints it to a branded PDF in their language, calendar and digits,
/// and emails it through the installation's mail server from the tenant (sender domain and DKIM of
/// task F1), with retries like notification emails. Every email sent is audited. The delivery log
/// is kept 30 days. Several workers share the work through leases.
/// </summary>
public sealed partial class ReportScheduleJob(
    IServiceScopeFactory scopes, NotificationOptions options, IPdfRenderer renderer, PdfOptions pdf, ILogger<ReportScheduleJob> logger, TimeProvider time)
    : BackgroundService
{
    /// <summary>How often the job runs.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the delivery log is kept.</summary>
    public static TimeSpan Retention { get; } = TimeSpan.FromDays(30);

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Queues due schedules and sends due emails now; returns how many emails were handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var handled = 0;
        foreach (var (tenantId, slug) in await TenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await ClaimAsync(tenantId, slug, "report_schedules", cancellationToken).ConfigureAwait(false) is { } schedule)
            {
                await QueueAsync(tenantId, slug, schedule, cancellationToken).ConfigureAwait(false);
            }

            while (await ClaimAsync(tenantId, slug, "report_deliveries", cancellationToken).ConfigureAwait(false) is { } delivery)
            {
                await SendAsync(tenantId, slug, delivery, cancellationToken).ConfigureAwait(false);
                handled++;
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var before = time.GetUtcNow() - Retention;
        await db.Database.SqlQuery<long>($"SELECT zelvo_purge_report_deliveries({before}) AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<List<(Guid, string)>> TenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_report_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    // Takes one due schedule or email with a lease.
    private async Task<Guid?> ClaimAsync(Guid tenantId, string slug, string table, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var now = time.GetUtcNow();
        var lease = now + Lease;
        var ids = table == "report_schedules"
            ? await db.Database.SqlQuery<Guid>($"""
                UPDATE report_schedules SET lease_until = {lease}
                WHERE id = (SELECT id FROM report_schedules
                            WHERE active AND next_run_at <= {now} AND (lease_until IS NULL OR lease_until < {now})
                            ORDER BY next_run_at LIMIT 1 FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await db.Database.SqlQuery<Guid>($"""
                UPDATE report_deliveries SET lease_until = {lease}, attempts = attempts + 1
                WHERE id = (SELECT id FROM report_deliveries
                            WHERE status = 'Pending' AND next_attempt_at <= {now} AND (lease_until IS NULL OR lease_until < {now})
                            ORDER BY next_attempt_at LIMIT 1 FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count == 0 ? null : ids[0];
    }

    // One email per recipient for the run that was due; the schedule moves to its next run.
    private async Task QueueAsync(Guid tenantId, string slug, Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var schedule = await db.ReportSchedules.AsNoTracking().Include(s => s.Recipients).SingleAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        var zone = await new ReportBuilder(db).TimeZoneAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var runAt = schedule.NextRunAt ?? now;
        var (from, to) = ReportScheduleTimes.Period(schedule.Frequency, runAt, zone);
        var queued = await db.ReportDeliveries.Where(d => d.ScheduleId == id && d.RunAt == runAt).Select(d => d.RecipientId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var recipient in schedule.Recipients.Where(r => !queued.Contains(r.UserId)))
        {
            db.ReportDeliveries.Add(new ReportDelivery { ScheduleId = id, RecipientId = recipient.UserId, RunAt = runAt, PeriodFrom = from, PeriodTo = to, NextAttemptAt = now });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // Moving the schedule on is bookkeeping, not a change by a person: no audit entry.
        var next = ReportScheduleTimes.NextRun(schedule.Frequency, schedule.DayOfWeek, schedule.DayOfMonth, schedule.TimeOfDay, zone, now);
        await db.ReportSchedules.Where(s => s.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(s => s.LastRunAt, runAt)
            .SetProperty(s => s.NextRunAt, next)
            .SetProperty(s => s.LeaseUntil, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(Guid tenantId, string slug, Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ZelvoDbContext>();
        var delivery = await db.ReportDeliveries.SingleAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        var schedule = await db.ReportSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == delivery.ScheduleId, cancellationToken).ConfigureAwait(false);
        var recipient = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == delivery.RecipientId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        delivery.LeaseUntil = null;

        // Access is checked when the report is made, so a person who lost it gets nothing.
        var permissions = await ReportAccess.PermissionsOfAsync(db, delivery.RecipientId, cancellationToken).ConfigureAwait(false);
        var scopeOfRecipient = ReportAccess.MayReceive(permissions)
            ? await ReportAccess.ScopeAsync(db, delivery.RecipientId, permissions, cancellationToken).ConfigureAwait(false)
            : null;
        var request = schedule is null ? null : ReportScheduleTimes.Request(schedule, delivery.PeriodFrom, delivery.PeriodTo);
        if (schedule is null || recipient is not { Status: UserStatus.Active, Email: { Length: > 0 } } || scopeOfRecipient is null || request is null)
        {
            delivery.Status = ReportDeliveryStatus.Skipped;
            delivery.Error = recipient is not { Status: UserStatus.Active } ? "The recipient is no longer active."
                : string.IsNullOrEmpty(recipient.Email) ? "The recipient has no email address."
                : request is null && schedule is not null ? "The schedule's report is not valid."
                : "The recipient may no longer see reports.";
            delivery.NextAttemptAt = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var composer = services.GetRequiredService<NotificationComposer>();
        var format = await composer.FormatAsync(recipient, cancellationToken).ConfigureAwait(false);
        EmailResult result;
        try
        {
            var (html, fileName) = await new ReportRenderer(db, time).HtmlAsync(request, scopeOfRecipient, recipient.Id, format.Language, pdf.FontsDirectory, cancellationToken)
                .ConfigureAwait(false);
            var content = await renderer.RenderAsync(html, cancellationToken).ConfigureAwait(false);
            var message = await EmailAsync(services, composer, schedule, delivery, request, recipient, format, cancellationToken).ConfigureAwait(false);
            result = await services.GetRequiredService<IEmailSender>()
                .SendAsync(message with { Attachments = [new EmailAttachment(fileName, "application/pdf", content)] }, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfRenderException exception)
        {
            LogReportFailed(logger, exception, id);
            result = new EmailResult(false, false, exception.Message);
        }

        delivery.Error = result.Error is { Length: > ReportDelivery.ErrorMaxLength } error ? error[..ReportDelivery.ErrorMaxLength] : result.Error;
        if (result.Sent)
        {
            delivery.Status = ReportDeliveryStatus.Sent;
            delivery.SentAt = now;
            delivery.NextAttemptAt = null;
            services.GetRequiredService<IAuditLog>().Add(AuditCategory.View, "report.emailed", "report_schedule", schedule.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["deliveryId"] = delivery.Id.ToString(), ["recipientId"] = recipient.Id.ToString(), ["report"] = schedule.Report,
                    ["from"] = delivery.PeriodFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["to"] = delivery.PeriodTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                });
        }
        else if (result.Permanent || delivery.Attempts > options.RetryDelays.Count)
        {
            delivery.Status = ReportDeliveryStatus.Failed;
            delivery.NextAttemptAt = null;
        }
        else
        {
            delivery.NextAttemptAt = now + options.RetryDelays[delivery.Attempts - 1];
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // The email: subject and wording from the report texts in the recipient's language, the
    // organisation's letterhead, a link to the dashboard with the same period and filters.
    private async Task<EmailMessage> EmailAsync(
        IServiceProvider services, NotificationComposer composer, ReportSchedule schedule, ReportDelivery delivery, ReportRequest request, UserAccount recipient,
        RecipientFormat format, CancellationToken cancellationToken)
    {
        var language = format.Language;
        var reportFormat = new ReportFormat(language, format.Calendar, format.Digits, format.TimeZone);
        var period = delivery.PeriodFrom == delivery.PeriodTo
            ? reportFormat.Day(delivery.PeriodFrom)
            : $"{reportFormat.Day(delivery.PeriodFrom)} – {reportFormat.Day(delivery.PeriodTo)}";
        var owner = await services.GetRequiredService<ZelvoDbContext>().Users.AsNoTracking().Where(u => u.Id == schedule.OwnerId)
            .Select(u => u.DisplayName ?? u.Email).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        // Text from people (the schedule's and owner's names) never forms placeholders.
        static string Plain(string text) => text.Replace("{{", "{ {", StringComparison.Ordinal);
        var template = new TemplateText(
            ReportTexts.Get(language, "emailSubject", ("name", Plain(schedule.Name)), ("period", period)),
            ReportTexts.Get(language, "emailIntro", ("name", Plain(schedule.Name)), ("period", period)) + "\n\n"
                + ReportTexts.Get(language, "emailWhy", ("owner", Plain(owner))),
            string.Empty,
            ReportTexts.Get(language, "emailOpen"));
        var path = request.Kind == ReportKind.Overview ? "reports" : $"reports/{request.Kind.ToString().ToLowerInvariant()}";
        var query = string.Create(CultureInfo.InvariantCulture, $"from={delivery.PeriodFrom:yyyy-MM-dd}&to={delivery.PeriodTo:yyyy-MM-dd}");
        var link = new Uri(options.WebUrl, $"{language}/{path}?{query}");
        var letterhead = await composer.LetterheadAsync(cancellationToken).ConfigureAwait(false);
        var rendered = NotificationRenderer.Render(template, new Dictionary<string, PlaceholderValue>(), format, letterhead, new NotificationLinks(link, null));
        return await services.GetRequiredService<NotificationProcessor>().MessageAsync(recipient.Email!, language, rendered.Subject, rendered.Html, rendered.Text,
            $"zelvo.report.{delivery.TenantId:N}.{delivery.Id:N}", cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending scheduled reports failed; trying again shortly.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled report email {Id} could not be made.")]
    private static partial void LogReportFailed(ILogger logger, Exception exception, Guid id);
}

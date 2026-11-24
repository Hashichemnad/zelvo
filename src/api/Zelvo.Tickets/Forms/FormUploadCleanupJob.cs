using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Tickets.Forms;

/// <summary>
/// Worker job (task C1): removes files uploaded for a form that was never sent. Form files are
/// uploaded while people fill the form; once the form is sent they belong to its ticket. Files
/// still unsent after <see cref="KeepFor"/> are deleted with their content.
/// </summary>
public sealed partial class FormUploadCleanupJob(IServiceScopeFactory scopes, ILogger<FormUploadCleanupJob> logger, TimeProvider time) : BackgroundService
{
    private const int BatchSize = 200;

    /// <summary>How long an unsent upload is kept.</summary>
    public static TimeSpan KeepFor { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often the job runs.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Removes what is due now; returns how many files were removed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow() - KeepFor;
        var removed = 0;
        foreach (var (tenantId, slug) in await TenantsAsync(before, cancellationToken).ConfigureAwait(false))
        {
            removed += await CleanTenantAsync(tenantId, slug, before, cancellationToken).ConfigureAwait(false);
        }

        return removed;
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

    private async Task<List<(Guid Id, string Slug)>> TenantsAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_tenants_with_stale_form_uploads(@before)", connection);
        command.Parameters.AddWithValue("before", before);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    private async Task<int> CleanTenantAsync(Guid tenantId, string slug, DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "forms.upload-cleanup", "Form upload clean-up");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();
        var stale = await db.TicketAttachments
            .Where(a => a.TicketId == null && a.ArticleId == null && a.ServiceId == null && a.UploadedAt < before)
            .OrderBy(a => a.UploadedAt).Take(BatchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var attachment in stale)
        {
            await store.RemoveAsync(attachment.Id, cancellationToken).ConfigureAwait(false);
        }

        db.TicketAttachments.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (stale.Count > 0)
        {
            LogRemoved(logger, stale.Count, slug);
        }

        return stale.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Removing unsent form uploads failed; trying again later.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} unsent form uploads of tenant {Tenant}.")]
    private static partial void LogRemoved(ILogger logger, int count, string tenant);
}

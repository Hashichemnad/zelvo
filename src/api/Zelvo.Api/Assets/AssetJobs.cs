using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Notifications;

namespace Zelvo.Api.Assets;

/// <summary>
/// Expiry alerts (task G3): contracts, warranties and software licences that end within the
/// tenant's warning period (default 30 days) are announced once per end date to the holders of
/// <c>assets.manage</c> and the item's owner, in the app and by email
/// (<c>asset.contract-expiring</c>, <c>asset.license-expiring</c>). Runs in the API every hour
/// under an advisory lock per tenant.
/// </summary>
internal sealed class AssetExpiryAlerts(ZelvoDbContext db, TenantSettingsStore settings, PendingNotifications notifications, IAuditLog audit)
{
    /// <summary>Most people told about one item.</summary>
    private const int MaxRecipients = 50;

    public async Task<int> RunAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var until = today.AddDays(await settings.GetAsync(AssetSettings.ExpiryWarningDays, cancellationToken).ConfigureAwait(false));
        var managers = await db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active && u.ErasedAt == null && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.AssetsManage)))
            .Select(u => u.Id).Take(MaxRecipients).ToListAsync(cancellationToken).ConfigureAwait(false);
        var activeOwners = new HashSet<Guid>(await db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active && u.ErasedAt == null).Select(u => u.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false));
        List<Guid> Recipients(Guid? owner) => [.. managers.Concat(owner is { } o && activeOwners.Contains(o) ? [o] : []).Distinct()];

        var sent = 0;
        var contracts = await db.AssetContracts.Where(c => c.EndsOn >= today && c.EndsOn <= until && (c.ExpiryAlertedFor == null || c.ExpiryAlertedFor != c.EndsOn))
            .OrderBy(c => c.EndsOn).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var contract in contracts)
        {
            var count = await db.AssetContractItems.CountAsync(i => i.ContractId == contract.Id, cancellationToken).ConfigureAwait(false);
            var recipients = Recipients(contract.OwnerId);
            if (recipients.Count > 0)
            {
                notifications.Add(new NotificationRequest(NotificationEvents.AssetContractExpiring, recipients, new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
                {
                    [Placeholders.ContractName] = PlaceholderValue.OfText(contract.Name),
                    [Placeholders.ContractKind] = PlaceholderValue.OfTerm($"asset.contractKind.{contract.Kind}"),
                    [Placeholders.ContractSupplier] = PlaceholderValue.OfText(contract.Supplier ?? "—"),
                    [Placeholders.ContractEndsOn] = PlaceholderValue.OfText(contract.EndsOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                    [Placeholders.ContractAssets] = new PlaceholderValue { Number = count },
                }, "/assets/contracts"));
            }

            contract.ExpiryAlertedFor = contract.EndsOn;
            audit.Add(AuditCategory.Admin, "assets.contract.expiry-alerted", "asset_contract", contract.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["endsOn"] = contract.EndsOn.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ["notified"] = recipients.Count });
            sent++;
        }

        var licenses = await db.SoftwareLicenses.Where(l => l.EndsOn != null && l.EndsOn >= today && l.EndsOn <= until && (l.ExpiryAlertedFor == null || l.ExpiryAlertedFor != l.EndsOn))
            .OrderBy(l => l.EndsOn).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var license in licenses)
        {
            var recipients = Recipients(license.OwnerId);
            if (recipients.Count > 0)
            {
                notifications.Add(new NotificationRequest(NotificationEvents.AssetLicenseExpiring, recipients, new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
                {
                    [Placeholders.LicenseName] = PlaceholderValue.OfText(license.Name),
                    [Placeholders.LicenseQuantity] = new PlaceholderValue { Number = license.Quantity },
                    [Placeholders.LicenseEndsOn] = PlaceholderValue.OfText(license.EndsOn!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                }, "/assets/licenses"));
            }

            license.ExpiryAlertedFor = license.EndsOn;
            audit.Add(AuditCategory.Admin, "assets.license.expiry-alerted", "software_license", license.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["endsOn"] = license.EndsOn!.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ["notified"] = recipients.Count });
            sent++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return sent;
    }
}

/// <summary>Runs <see cref="AssetExpiryAlerts"/> for every tenant with contracts or licences.</summary>
internal sealed partial class AssetJobs(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AssetJobs> logger, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Assets:Jobs:Enabled", true))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var (id, slug) in await TenantsAsync(stoppingToken))
                {
                    await RunTenantAsync(id, slug, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(configuration.GetValue("Assets:Jobs:Interval", TimeSpan.FromHours(1)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One tenant's alerts; -1 when another instance holds its lock.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, DateOnly today, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "assets.jobs", "Asset jobs");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await LockAsync(db, tenantId, "SELECT pg_try_advisory_lock(hashtext('zelvo.asset-jobs'), hashtext(@tenant))", cancellationToken))
            {
                return -1;
            }

            try
            {
                return await scope.ServiceProvider.GetRequiredService<AssetExpiryAlerts>().RunAsync(today, cancellationToken);
            }
            finally
            {
                await LockAsync(db, tenantId, "SELECT pg_advisory_unlock(hashtext('zelvo.asset-jobs'), hashtext(@tenant))", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> LockAsync(ZelvoDbContext db, Guid tenantId, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("tenant", tenantId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<List<(Guid Id, string Slug)>> TenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_asset_job_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The asset jobs failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

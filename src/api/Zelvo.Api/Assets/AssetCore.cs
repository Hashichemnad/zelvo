using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Cmdb;

namespace Zelvo.Api.Assets;

/// <summary>Tenant settings of asset management (task G3), changed in Settings → Assets.</summary>
internal static class AssetSettings
{
    /// <summary>Prefix of generated asset tags, for example <c>AST</c> in <c>AST-000123</c>.</summary>
    public static SettingDefinition<string> TagPrefix { get; } = new("assets.tagPrefix", "AST", Permissions.AssetsManage, Knowledge.KnowledgeSettings.IsPrefix);

    /// <summary>How many days before a contract, warranty or licence ends the alert goes out.</summary>
    public static SettingDefinition<int> ExpiryWarningDays { get; } = new("assets.expiryWarningDays", 30, Permissions.AssetsManage, d => d is >= 1 and <= 365);
}

/// <summary>
/// The asset lifecycle (module 18): requested → ordered → received → in stock → assigned → in
/// repair → retired → disposed, with the moves between them that make sense, and the status the
/// configuration item takes in each state.
/// </summary>
internal static class AssetLifecycle
{
    private static readonly Dictionary<AssetState, AssetState[]> Moves = new()
    {
        [AssetState.Requested] = [AssetState.Ordered, AssetState.Received, AssetState.InStock, AssetState.Retired],
        [AssetState.Ordered] = [AssetState.Received, AssetState.Retired],
        [AssetState.Received] = [AssetState.InStock, AssetState.Assigned, AssetState.InRepair],
        [AssetState.InStock] = [AssetState.Assigned, AssetState.InRepair, AssetState.Retired],
        [AssetState.Assigned] = [AssetState.InStock, AssetState.InRepair, AssetState.Retired, AssetState.Assigned],
        [AssetState.InRepair] = [AssetState.InStock, AssetState.Assigned, AssetState.Retired],
        [AssetState.Retired] = [AssetState.InStock, AssetState.Disposed],
        [AssetState.Disposed] = [],
    };

    /// <summary>Whether an asset may go from <paramref name="from"/> to <paramref name="to"/> (Assigned → Assigned hands it to someone else).</summary>
    public static bool CanMove(AssetState from, AssetState to) => Moves[from].Contains(to);

    /// <summary>The states an asset can move to next.</summary>
    public static IReadOnlyList<AssetState> Next(AssetState from) => Moves[from];

    /// <summary>The configuration item's status in each state.</summary>
    public static CiStatus StatusOf(AssetState state) => state switch
    {
        AssetState.Requested or AssetState.Ordered or AssetState.Received => CiStatus.Planned,
        AssetState.InRepair => CiStatus.Maintenance,
        AssetState.Retired or AssetState.Disposed => CiStatus.Retired,
        _ => CiStatus.Active,
    };

    /// <summary>Only hardware and software items are assets.</summary>
    public static bool IsAssetFamily(CiFamily family) => family is CiFamily.Hardware or CiFamily.Software;
}

/// <summary>Asset tags: capital letters, digits and hyphens, at most 32; generated ones are <c>AST-000123</c>.</summary>
internal static partial class AssetTags
{
    public static bool IsTag(string? tag) => tag is { Length: >= 1 and <= Asset.TagMaxLength } && TagPattern().IsMatch(tag);

    public static async Task<string> NextAsync(ZelvoDbContext db, Guid tenantId, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var prefix = await settings.GetAsync(AssetSettings.TagPrefix, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var values = await db.Database.SqlQuery<long>($"""
                INSERT INTO asset_tag_sequences (tenant_id, last_value)
                VALUES ({tenantId}, 1)
                ON CONFLICT (tenant_id) DO UPDATE SET last_value = asset_tag_sequences.last_value + 1
                RETURNING last_value AS "Value"
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
            var tag = Zelvo.Tickets.TicketNumbers.Format(prefix, values.Single());
            // A tag typed by hand may already use the next number.
            if (!await db.Assets.AnyAsync(a => a.Tag == tag, cancellationToken).ConfigureAwait(false))
            {
                return tag;
            }
        }

        throw new InvalidOperationException("No free asset tag was found.");
    }

    [GeneratedRegex("^[A-Z0-9]([A-Z0-9-]{0,30}[A-Z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex TagPattern();
}

/// <summary>
/// Software licence compliance (task G3): what G2's discovery found installed against what the
/// organisation owns, per licence. Devices count once each; for licences per person, devices
/// assigned to the same person count once, and devices assigned to nobody once each. Retired and
/// merged items are left out.
/// </summary>
internal static class LicenseCompliance
{
    public sealed record Usage(int Devices, int People, int Used, int Shortfall, int Spare);

    public static IReadOnlyList<string> Products(SoftwareLicense license)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(license.ProductKeys) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task<Dictionary<Guid, Usage>> ForAsync(ZelvoDbContext db, IReadOnlyList<SoftwareLicense> licenses, CancellationToken cancellationToken)
    {
        var keys = licenses.SelectMany(Products).Distinct(StringComparer.Ordinal).ToList();
        var installs = keys.Count == 0
            ? []
            : await (from s in db.CiInstalledSoftwareItems.AsNoTracking()
                     join i in db.ConfigurationItems.AsNoTracking() on s.ConfigurationItemId equals i.Id
                     where keys.Contains(s.ProductKey) && i.Status != CiStatus.Retired && i.MergedIntoId == null
                     join a in db.Assets.AsNoTracking() on i.Id equals a.ConfigurationItemId into assets
                     from a in assets.DefaultIfEmpty()
                     select new { s.ProductKey, s.ConfigurationItemId, AssignedToId = a == null ? null : a.AssignedToId })
                .Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<Guid, Usage>();
        foreach (var license in licenses)
        {
            var products = Products(license).ToHashSet(StringComparer.Ordinal);
            var rows = installs.Where(i => products.Contains(i.ProductKey)).DistinctBy(i => i.ConfigurationItemId).ToList();
            var devices = rows.Count;
            var people = rows.Where(r => r.AssignedToId is not null).Select(r => r.AssignedToId).Distinct().Count() + rows.Count(r => r.AssignedToId is null);
            var used = license.Metric == LicenseMetric.User ? people : devices;
            result[license.Id] = new Usage(devices, people, used, Math.Max(0, used - license.Quantity), Math.Max(0, license.Quantity - used));
        }

        return result;
    }
}

/// <summary>Who sees what of asset management.</summary>
internal static class AssetAccess
{
    public static bool IsReader(CurrentUser user) => user.Has(Permissions.AssetsRead) || user.Has(Permissions.AssetsManage);
}

/// <summary>The latest warranty end of each asset (for lists and the customer panel).</summary>
internal static class AssetWarranties
{
    public static async Task<Dictionary<Guid, DateOnly>> EndsAsync(ZelvoDbContext db, IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0)
        {
            return [];
        }

        var ids = assetIds.ToList();
        return (await (from item in db.AssetContractItems.AsNoTracking()
                       join contract in db.AssetContracts.AsNoTracking() on item.ContractId equals contract.Id
                       where ids.Contains(item.ConfigurationItemId) && contract.Kind == AssetContractKind.Warranty
                       select new { item.ConfigurationItemId, contract.EndsOn }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(r => r.ConfigurationItemId).ToDictionary(g => g.Key, g => g.Max(r => r.EndsOn));
    }
}

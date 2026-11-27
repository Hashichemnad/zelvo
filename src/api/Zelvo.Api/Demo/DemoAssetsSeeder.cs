using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Assets;
using Zelvo.Api.Cmdb;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Cmdb;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task G3: each sample organisation's assets: the laptops and desktops of the CMDB as assets with
/// tags, one assigned to the sample requester (shown on their tickets and in My assets) with a
/// warranty that ends within the alert period, one in repair and one in stock; a support contract
/// on servers; and Microsoft 365 licences with a shortfall against what discovery found installed
/// (the company) or with spare entitlements (the government entity). Fictional.
/// </summary>
internal sealed class DemoAssetsSeeder : IDemoSeeder
{
    public string Key => "g3-assets";

    public int Order => 274;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var tenant = await db.Tenants.SingleAsync(cancellationToken).ConfigureAwait(false);
        var items = await db.ConfigurationItems.Where(i => i.MergedIntoId == null && i.Status != CiStatus.Retired).ToListAsync(cancellationToken).ConfigureAwait(false);
        ConfigurationItem? Item(string name) => items.FirstOrDefault(i => i.Name == name);
        var requester = context.Organisation.User("requester") is { } demo
            ? await db.Users.AsNoTracking().Where(u => u.Email == context.Organisation.UsernameOf(demo)).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var admin = context.Organisation.User("admin") is { } a
            ? await db.Users.AsNoTracking().Where(u => u.Email == context.Organisation.UsernameOf(a)).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var plan = government
            ? new (string Name, AssetState State, bool Mine, string Supplier)[] { ("PC-CS-015", AssetState.Assigned, true, "Fictional Computers LLC"), ("Customer service printer", AssetState.InStock, false, "Fictional Print Co.") }
            : [("LT-0042", AssetState.Assigned, true, "Fictional Laptops FZ-LLC"), ("LT-0051", AssetState.InRepair, false, "Fictional Laptops FZ-LLC"), ("LT-0052", AssetState.InStock, false, "Fictional Laptops FZ-LLC")];
        var assets = new List<Asset>();
        foreach (var (name, state, mine, supplier) in plan)
        {
            if (Item(name) is not { } item || (state == AssetState.Assigned && requester is null))
            {
                continue;
            }

            var asset = new Asset
            {
                TenantId = tenant.Id, ConfigurationItemId = item.Id, Tag = await AssetTags.NextAsync(db, tenant.Id, settings, cancellationToken).ConfigureAwait(false),
                State = state, AssignedToId = mine ? requester : null, AssignedAt = mine ? DateTimeOffset.UtcNow.AddMonths(-8) : null,
                PurchaseDate = today.AddMonths(-11), PurchaseCost = government ? 3_450m : 4_200m, Supplier = supplier, OrderNumber = $"PO-2026-{100 + assets.Count:000}",
            };
            db.Assets.Add(asset);
            item.Status = AssetLifecycle.StatusOf(state);
            assets.Add(asset);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (assets.Count > 0)
        {
            // The assigned one's warranty ends within the alert period; the others' next year.
            var soon = new AssetContract
            {
                TenantId = tenant.Id, Kind = AssetContractKind.Warranty, Name = government ? "Desktop warranty, 1 year" : "Laptop warranty, 1 year",
                Supplier = plan[0].Supplier, Reference = "WR-48151", StartsOn = today.AddMonths(-11), EndsOn = today.AddDays(20), OwnerId = admin,
            };
            var later = new AssetContract
            {
                TenantId = tenant.Id, Kind = AssetContractKind.Warranty, Name = government ? "Printer warranty, 3 years" : "Laptop warranty, 3 years", Supplier = plan[^1].Supplier,
                Reference = "WR-62342", StartsOn = today.AddMonths(-11), EndsOn = today.AddYears(2), OwnerId = admin,
            };
            db.AssetContracts.AddRange(soon, later);
            db.AssetContractItems.Add(new AssetContractItem { TenantId = tenant.Id, ContractId = soon.Id, ConfigurationItemId = assets[0].ConfigurationItemId });
            foreach (var other in assets.Skip(1))
            {
                db.AssetContractItems.Add(new AssetContractItem { TenantId = tenant.Id, ContractId = later.Id, ConfigurationItemId = other.ConfigurationItemId });
            }
        }

        // A support contract (not an asset contract item: the servers are not assets) for the data centre.
        db.AssetContracts.Add(new AssetContract
        {
            TenantId = tenant.Id, Kind = AssetContractKind.Support, Name = government ? "Server hardware support" : "Cloud support plan", Supplier = "Fictional Systems LLC",
            Reference = "SUP-2026-07", StartsOn = today.AddMonths(-5), EndsOn = today.AddMonths(7), CostAed = government ? 48_000m : 36_000m, OwnerId = admin,
        });

        // Licences of the software discovery found (G2): the company owns fewer than it uses.
        var m365 = CmdbSoftware.ProductKey("Microsoft 365 Apps for enterprise", "Microsoft Corporation");
        db.SoftwareLicenses.Add(new SoftwareLicense
        {
            TenantId = tenant.Id, Name = "Microsoft 365 Apps for enterprise", Publisher = "Microsoft Corporation", ProductKeys = JsonSerializer.Serialize(new[] { m365 }),
            Metric = LicenseMetric.Device, Quantity = government ? 25 : 2, StartsOn = today.AddMonths(-3), EndsOn = today.AddMonths(9), Supplier = "Fictional Licensing Partner",
            Reference = "EA-7781", CostAed = government ? 11_250m : 900m, OwnerId = admin,
        });
        if (!government)
        {
            db.SoftwareLicenses.Add(new SoftwareLicense
            {
                TenantId = tenant.Id, Name = "Fictional CRM Desktop", Publisher = "Fictional Software", Quantity = 5, Metric = LicenseMetric.User,
                ProductKeys = JsonSerializer.Serialize(new[] { CmdbSoftware.ProductKey("Fictional CRM Desktop", "Fictional Software") }), Supplier = "Fictional Software", OwnerId = admin,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Api.Assets;
using Zelvo.Data.Branding;
using Zelvo.Data.Identity;
using Zelvo.Tickets.Exports;

namespace Zelvo.Api.Tests;

/// <summary>Assets, their lifecycle, contracts and warranties, licence compliance, expiry alerts and labels (task G3, module 18), against PostgreSQL.</summary>
public sealed class AssetApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithMasterKey();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private sealed class RecordingRenderer : IPdfRenderer
    {
        public List<string> Html { get; } = [];

        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken)
        {
            Html.Add(html);
            return Task.FromResult("%PDF-1.7"u8.ToArray());
        }
    }

    private static async Task<JsonObject> AssetAsync(HttpClient admin, Guid item, object? extra = null)
    {
        var body = new JsonObject { ["configurationItemId"] = item.ToString() };
        foreach (var (key, value) in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra ?? new { }))!.AsObject())
        {
            body[key] = value?.DeepClone();
        }

        return await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assets", body, HttpStatusCode.Created);
    }

    private static async Task<JsonObject> MoveAsync(HttpClient admin, JsonObject asset, string state, Guid? person = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/assets/{asset.Id()}/state")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { state, assignedToId = person }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", (string)asset["etag"]!);
        using var response = await admin.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
    }

    [Fact]
    public async Task Assets_follow_their_lifecycle_are_assigned_to_people_and_show_on_their_tickets_and_in_my_assets()
    {
        var tenant = await database.CreateTenantAsync("ast");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        var classes = await admin.StandardClassesAsync();
        var laptop = await admin.ItemAsync(classes["laptop"], "LT-0042", new { serialNumber = "SN-42" });
        var service = await admin.ItemAsync(classes["business-service"], "Email");

        // Only hardware and software items are assets; tags are generated and unique.
        using var notHardware = await admin.SendAsync(HttpMethod.Post, "/api/v1/assets", new { configurationItemId = service });
        Assert.Equal("not-an-asset-class", (await notHardware.ProblemAsync()).Code);
        using var noPerson = await admin.SendAsync(HttpMethod.Post, "/api/v1/assets", new { configurationItemId = laptop, state = "Assigned" });
        Assert.Equal(HttpStatusCode.BadRequest, noPerson.StatusCode);
        var asset = await AssetAsync(admin, laptop, new { state = "Ordered", supplier = "Fictional Laptops", purchaseCost = 4200.50m });
        Assert.Equal("AST-000001", (string)asset["tag"]!);
        Assert.Equal("Planned", (string)asset["itemStatus"]!);
        Assert.Equal(["Received", "Retired"], asset["nextStates"]!.AsArray().Select(s => (string)s!));
        var second = await AssetAsync(admin, await admin.ItemAsync(classes["laptop"], "LT-0043"), new { tag = "lt-9" });
        Assert.Equal("LT-9", (string)second["tag"]!);
        using var taken = await admin.SendAsync(HttpMethod.Post, "/api/v1/assets", new { configurationItemId = await admin.ItemAsync(classes["laptop"], "LT-0044"), tag = "LT-9" });
        Assert.Equal("tag-taken", (await taken.ProblemAsync()).Code);

        // Ordered → received → assigned: the item becomes active; skipping steps is refused.
        using (var skip = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/assets/{laptop}/state") { Content = System.Net.Http.Json.JsonContent.Create(new { state = "Disposed" }) })
        {
            Assert.Equal("state-not-allowed", (await (await admin.SendAsync(skip, Ct)).ProblemAsync()).Code);
        }

        asset = await MoveAsync(admin, asset, "Received");
        asset = await MoveAsync(admin, asset, "Assigned", requesterUser.Id);
        Assert.Equal(("Assigned", "Active"), ((string)asset["state"]!, (string)asset["itemStatus"]!));
        Assert.Equal(requesterUser.Id, Guid.Parse((string)asset["assignedTo"]!["id"]!));
        Assert.Contains(await database.AuditAsync(tenant), e => e.Action == "assets.state-changed");
        var item = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{laptop}");
        Assert.Equal("Active", (string)item["status"]!);

        // A warranty on it.
        var warranty = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assets/contracts", new
        {
            kind = "Warranty", name = "Laptop warranty", supplier = "Fictional Laptops", endsOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(200)), itemIds = new[] { laptop },
        }, HttpStatusCode.Created);
        Assert.Equal(1, (int)warranty["itemCount"]!);

        // The agent sees the requester's laptop, warranty and tickets beside the incident (B4 customer panel).
        var incident = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Laptop does not start", requesterId = requesterUser.Id }));
        await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{incident}/configuration-items/{laptop}");
        var customer = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}/customer");
        var assets = customer["assets"]!.AsArray();
        Assert.Equal("AST-000001", (string)assets[0]!["tag"]!);
        Assert.Equal(1, (int)assets[0]!["ticketCount"]!);
        Assert.NotNull((string?)assets[0]!["warrantyEndsOn"]);
        Assert.Single(await agent.ArrayAsync($"/api/v1/users/{requesterUser.Id}/assets"));

        // The requester sees it under My assets, without ticket counts; never the asset list.
        var mine = await requester.ArrayAsync("/api/v1/me/assets");
        Assert.Equal("LT-0042", (string)mine.Single()!["name"]!);
        Assert.Null(mine[0]!["ticketCount"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Get, "/api/v1/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/assets/contracts", new { name = "x", endsOn = "2030-01-01" })).StatusCode);

        // A scanned label finds it; the list filters by person and warranties.
        var found = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/assets/lookup?tag=ast-000001");
        Assert.Equal(laptop, found.Id());
        Assert.Equal(HttpStatusCode.NotFound, (await agent.SendAsync(HttpMethod.Get, "/api/v1/assets/lookup?tag=NOPE")).StatusCode);
        Assert.Equal(1, (int)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/assets?assignedToId={requesterUser.Id}"))["totalCount"]!);

        // Back to stock, then retired and disposed: the item is retired; disposed is final.
        asset = await MoveAsync(admin, await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/assets/{laptop}"), "InStock");
        Assert.Null(asset["assignedTo"]);
        asset = await MoveAsync(admin, asset, "Retired");
        asset = await MoveAsync(admin, asset, "Disposed");
        Assert.Empty(asset["nextStates"]!.AsArray());
        Assert.Equal("Retired", (string)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{laptop}"))["status"]!);
        Assert.Empty(await requester.ArrayAsync("/api/v1/me/assets"));
    }

    [Fact]
    public async Task Licences_are_compared_with_discovered_installations_and_contracts_and_licences_alert_once_before_they_end()
    {
        var tenant = await database.CreateTenantAsync("asl");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var classes = await admin.StandardClassesAsync();
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);

        // Discovery found Microsoft 365 on three laptops; two of them belong to the same person.
        var source = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/discovery/sources", new { name = "Agent", kind = "DiscoveryApi" }, HttpStatusCode.Created);
        var m365 = new[] { new { name = "Microsoft 365 Apps", publisher = "Microsoft", version = "16.0" } };
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/discovery/sources/{source.Id()}/records", new
        {
            records = new object[]
            {
                new { id = "a", @class = "laptop", name = "LT-1", attributes = new { serialNumber = "S1" }, software = m365 },
                new { id = "b", @class = "laptop", name = "LT-2", attributes = new { serialNumber = "S2" }, software = m365 },
                new { id = "c", @class = "laptop", name = "LT-3", attributes = new { serialNumber = "S3" }, software = m365 },
            },
        });
        var software = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/assets/software");
        var product = software["items"]![0]!;
        Assert.Equal(3, (int)product["devices"]!);
        var key = (string)product["productKey"]!;
        var items = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?q=LT-"))["items"]!.AsArray().ToDictionary(i => (string)i!["name"]!, i => Guid.Parse((string)i!["id"]!));
        foreach (var name in new[] { "LT-1", "LT-2" })
        {
            await AssetAsync(admin, items[name], new { state = "Assigned", assignedToId = person.Id });
        }

        var perDevice = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assets/licenses",
            new { name = "Microsoft 365 per device", productKeys = new[] { key }, metric = "Device", quantity = 2 }, HttpStatusCode.Created);
        Assert.Equal((3, 3, 1, 0), ((int)perDevice["devices"]!, (int)perDevice["used"]!, (int)perDevice["shortfall"]!, (int)perDevice["spare"]!));
        var perUser = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assets/licenses",
            new { name = "Microsoft 365 per user", productKeys = new[] { key }, metric = "User", quantity = 5, endsOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)) },
            HttpStatusCode.Created);
        // The person's two laptops count once, the unassigned one once.
        Assert.Equal((2, 0, 3), ((int)perUser["used"]!, (int)perUser["shortfall"]!, (int)perUser["spare"]!));
        using var noProducts = await admin.SendAsync(HttpMethod.Post, "/api/v1/assets/licenses", new { name = "x", quantity = 1, productKeys = Array.Empty<string>() });
        Assert.Equal("products-required", (await noProducts.ProblemAsync()).Code);
        var asset = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/assets/{items["LT-1"]}");
        Assert.Equal(2, asset["licenses"]!.AsArray().Count);

        // A contract ending in 10 days and one next year; the alert goes out once per end date.
        var soon = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assets/contracts", new
        {
            kind = "Support", name = "Laptop support", supplier = "Fictional Support", endsOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), itemIds = new[] { items["LT-1"] },
        }, HttpStatusCode.Created);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assets/contracts", new { name = "Lease", kind = "Lease", endsOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(300)) },
            HttpStatusCode.Created);
        Assert.Single(await admin.ArrayAsync("/api/v1/assets/contracts?expiring=true"));
        var jobs = _factory.Services.GetRequiredService<AssetJobs>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(2, await jobs.RunTenantAsync(tenant.Id, tenant.Slug, today, Ct));
        Assert.Equal(0, await jobs.RunTenantAsync(tenant.Id, tenant.Slug, today, Ct));
        await using (var db = TestData.AppContext(database, tenant))
        {
            var sent = await db.Notifications.AsNoTracking().Where(n => n.EventType.StartsWith("asset.")).ToListAsync(Ct);
            Assert.Contains(sent, n => n.EventType == "asset.contract-expiring");
            Assert.Contains(sent, n => n.EventType == "asset.license-expiring");
        }

        // A new end date within the period gets a new alert.
        using (var change = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/assets/contracts/{soon.Id()}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { endsOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(12)) }),
        })
        {
            Assert.True((await admin.SendAsync(change, Ct)).IsSuccessStatusCode);
        }

        Assert.Equal(1, await jobs.RunTenantAsync(tenant.Id, tenant.Slug, today, Ct));
        Assert.Contains(await database.AuditAsync(tenant), e => e.Action == "assets.contract.expiry-alerted");

        // Settings: the prefix of new tags and the warning period.
        var settings = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assets/settings", new { tagPrefix = "IT", expiryWarningDays = 60 });
        Assert.Equal(("IT", 60), ((string)settings["tagPrefix"]!, (int)settings["expiryWarningDays"]!));
        var third = await AssetAsync(admin, items["LT-3"]);
        Assert.StartsWith("IT-", (string)third["tag"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Label_sheets_are_made_by_the_worker_with_a_qr_code_and_code_128_of_each_tag()
    {
        var tenant = await database.CreateTenantAsync("asb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var classes = await admin.StandardClassesAsync();
        var one = await AssetAsync(admin, await admin.ItemAsync(classes["laptop"], "LT-LABEL-1"), new { tag = "LBL-0001" });
        var two = await AssetAsync(admin, await admin.ItemAsync(classes["desktop"], "PC-LABEL-2"), new { tag = "LBL-0002" });

        using var none = await admin.SendAsync(HttpMethod.Post, "/api/v1/assets/labels/exports?ids=", new { language = "en" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        var export = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/assets/labels/exports?ids={one.Id()},{two.Id()}&layout=Large", new { language = "ar" },
            HttpStatusCode.Accepted);
        Assert.Contains(await database.AuditAsync(tenant), e => e.Action == "assets.labels.requested");

        var renderer = new RecordingRenderer();
        var job = new DocumentExportJob(_factory.Services.GetRequiredService<IServiceScopeFactory>(), renderer, new PdfOptions(), NullLogger<DocumentExportJob>.Instance, TimeProvider.System);
        Assert.True(await job.RunOnceAsync(Ct) >= 1);
        var html = renderer.Html.Single(h => h.Contains("LBL-0001", StringComparison.Ordinal));
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("LBL-0002", html, StringComparison.Ordinal);
        Assert.Contains("PC-LABEL-2", html, StringComparison.Ordinal);
        Assert.Equal(4, html.Split("<svg", StringSplitOptions.None).Length - 1);
        Assert.Contains("repeat(2, 99mm)", html, StringComparison.Ordinal);
        var ready = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/exports/{export.Id()}");
        Assert.Equal("Ready", (string?)ready["status"]);

        // Real Chromium, when this computer has it, makes the PDF.
        var options = new PdfOptions();
        if (File.Exists(options.ChromiumPath))
        {
            var pdf = await new ChromiumPdfRenderer(options).RenderAsync(html, Ct);
            Assert.Equal("%PDF-"u8.ToArray(), pdf[..5]);
        }

        await using var db = TestData.AppContext(database, tenant);
        Assert.Equal(ExportKind.AssetLabels, (await db.DocumentExports.AsNoTracking().SingleAsync(Ct)).Kind);
    }
}

/// <summary>The QR code and Code 128 encoders of asset labels (task G3).</summary>
public sealed class BarcodeTests
{
    // Checked with a reference decoder (ZXing-C++), which reads it as AST-000123.
    private static readonly string[] AstQr =
    [
        "111111100111101111111", "100000101001001000001", "101110100110001011101", "101110100010101011101", "101110101011101011101", "100000100111001000001",
        "111111101010101111111", "000000000110000000000", "101010100110100010010", "101100001001010011000", "011101110101011011011", "001101010011110100000",
        "111011100101010011101", "000000001100010010010", "111111100000100010111", "100000100010010101011", "101110101100110010000", "101110100001010010110",
        "101110101011010011101", "100000100001110101010", "111111101101001011011",
    ];

    [Fact]
    public void Qr_codes_are_byte_mode_level_m_with_finders_timing_and_format()
    {
        var matrix = Barcodes.QrMatrix("AST-000123");
        Assert.Equal(21, matrix.GetLength(0));
        var rows = Enumerable.Range(0, 21).Select(r => string.Concat(Enumerable.Range(0, 21).Select(c => matrix[r, c] ? '1' : '0'))).ToArray();
        Assert.Equal(AstQr, rows);
        // Longer tags take bigger versions; more than 42 bytes do not fit.
        Assert.Equal(25, Barcodes.QrMatrix("DEMO-LT-0042-XYZ-123456789").GetLength(0));
        Assert.Equal(29, Barcodes.QrMatrix(new string('A', 42)).GetLength(0));
        Assert.Throws<ArgumentException>(() => Barcodes.QrMatrix(new string('A', 43)));
        var svg = Barcodes.QrSvg("AST-000123", 25);
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("width=\"25mm\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_128_uses_code_set_b_with_its_check_symbol()
    {
        // Start B (104), A S T - 0 0 0 1 2 3, check (104 + Σ value × position) mod 103 = 90, stop.
        Assert.Equal([104, 33, 51, 52, 13, 16, 16, 16, 17, 18, 19, 90, 106], Barcodes.Code128Values("AST-000123"));
        Assert.StartsWith("211214", Barcodes.Code128Widths("AST-000123"), StringComparison.Ordinal);
        Assert.EndsWith("2331112", Barcodes.Code128Widths("AST-000123"), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Barcodes.Code128Values("é"));
        Assert.Contains("<path", Barcodes.Code128Svg("AST-000123", 40, 10), StringComparison.Ordinal);
    }
}

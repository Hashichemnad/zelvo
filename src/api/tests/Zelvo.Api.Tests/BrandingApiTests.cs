using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Zelvo.Data.Branding;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;
using Zelvo.Tickets.Branding;
using Zelvo.Tickets.Exports;

namespace Zelvo.Api.Tests;

/// <summary>A DNS that answers what the test puts in it.</summary>
internal sealed class FakeDns : IDnsLookup
{
    public ConcurrentDictionary<string, List<string>> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Txt.TryGetValue(name, out var records) ? [.. records] : []);
}

/// <summary>White-label branding (module 11, task F1) against PostgreSQL.</summary>
public sealed class BrandingApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly FakeDns _dns = new();
    private readonly string _tlsToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory Factory => _factoryLazy ??= ApiFactory.ForDatabase(database).WithMasterKey()
        .WithSetting("Branding:TlsToken", _tlsToken)
        .WithSetting("Branding:PublicHost", "zelvo.example.ae")
        .WithSetting("Branding:SpfInclude", "include:spf.mail.example.ae")
        .WithServices(services =>
        {
            services.RemoveAll<IDnsLookup>();
            services.AddSingleton<IDnsLookup>(_dns);
        });

    private ApiFactory? _factoryLazy;

    public ValueTask DisposeAsync() => _factoryLazy?.DisposeAsync() ?? ValueTask.CompletedTask;

    // A tiny valid PNG (1 × 1), built at run time.
    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0xF8, 0x0F, 0x00, 0x00,
        0x01, 0x01, 0x00, 0x05, 0x18, 0xD8, 0x4D, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string slot, byte[] content, string fileName, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        return await client.PostAsync(new Uri($"/api/v1/settings/branding/images/{slot}", UriKind.Relative), form, Ct);
    }

    [Fact]
    public void Contrast_follows_wcag()
    {
        Assert.Equal(21, BrandingValue.Contrast("#000000", "#ffffff"));
        Assert.All(BrandingValue.Default.Checks(), c => Assert.True(c.Passed, c.Key));
        var yellow = BrandingValue.Default with { LightColour = "#ffd400" };
        Assert.Contains(yellow.Checks(), c => c.Key == "light.text" && !c.Passed);
        Assert.False(BrandingValue.IsColour("red"));
        Assert.Equal("Zelvo", BrandingValue.Default.ProductNameIn("en"));
        Assert.Equal("زيلفو", BrandingValue.Default.ProductNameIn("ar"));
    }

    [Fact]
    public async Task Administrators_draft_preview_and_publish_branding_that_passes_the_contrast_checks()
    {
        var tenant = await database.CreateTenantAsync("f1pub");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var visitor = Factory.ForTenant(tenant);

        // Zelvo's default look until something is published.
        var before = await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/branding");
        Assert.Equal(BrandingValue.DefaultLightColour, (string?)before["lightColour"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/settings/branding")).StatusCode);

        // A colour that fails contrast: saved as a draft, but not published.
        var draft = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/branding",
            new { productName = new { en = "Municipality Service Desk", ar = "مكتب خدمات البلدية" }, lightColour = "#FFD400", font = "cairo" });
        Assert.Contains(draft["checks"]!.AsArray(), c => (string?)c!["key"] == "light.text" && !(bool)c["passed"]!);
        var refused = await admin.SendAsync(HttpMethod.Post, "/api/v1/settings/branding/publish");
        Assert.Equal("branding-not-ready", (await refused.ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/branding", new { darkColour = "blue" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/branding", new { font = "comic-sans" })).StatusCode);
        // Nobody sees the draft yet.
        Assert.Equal(BrandingValue.DefaultLightColour, (string?)(await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/branding"))["lightColour"]);

        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/branding", new { lightColour = "#7a1f3d", darkColour = "#f2b8c6" });
        var published = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/settings/branding/publish");
        Assert.False((bool)published["unpublished"]!);
        var after = await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/branding");
        Assert.Equal("مكتب خدمات البلدية", (string?)after["productName"]!["ar"]);
        Assert.Equal("#7a1f3d", (string?)after["lightColour"]);
        Assert.Equal("Cairo", (string?)after["fontFamily"]);
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "branding.published");

        // The sign-in pages follow: product name, encoded.
        var (realm, branding) = Assert.Single(Factory.Keycloak.BrandingCalls);
        Assert.Equal($"zelvo-{tenant.Slug}", realm);
        Assert.Equal("Municipality Service Desk", branding.DisplayName);
        Assert.Contains("http://localhost:3000", branding.WebUrls);
    }

    [Fact]
    public async Task Images_are_checked_scanned_and_served_only_once_published()
    {
        var tenant = await database.CreateTenantAsync("f1img");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var visitor = Factory.ForTenant(tenant);

        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        Assert.Equal("file-type-not-allowed", (await (await UploadAsync(admin, "LogoLight", svg, "logo.svg", "image/svg+xml")).ProblemAsync()).Code);
        Assert.Equal("file-content-mismatch", (await (await UploadAsync(admin, "LogoLight", [0xFF, 0xD8, 0xFF, 0x00], "logo.png", "image/png")).ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(admin, "Banner", Png(), "logo.png", "image/png")).StatusCode);

        using var uploaded = await UploadAsync(admin, "LogoLight", Png(), "logo.png", "image/png");
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var settings = (await uploaded.JsonAsync()).AsObject();
        Assert.Equal("Pending", (string?)settings["images"]!["logoLight"]!["scanStatus"]);
        Assert.Contains("image:logoLight:Pending", settings["problems"]!.AsArray().Select(p => (string)p!));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, "/api/v1/settings/branding/publish")).StatusCode);

        // The worker's scan finds it clean.
        await using (var db = TestData.AppContext(database, tenant))
        {
            await db.TicketAttachments.Where(a => a.BrandingSlot == BrandingSlot.LogoLight).ExecuteUpdateAsync(set => set.SetProperty(a => a.ScanStatus, AttachmentScanStatus.Clean), Ct);
        }

        // The draft image is a preview for administrators only.
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(new Uri("/api/v1/branding/images/LogoLight?draft=true", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/api/v1/branding/images/LogoLight?draft=true", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(new Uri("/api/v1/branding/images/LogoLight", UriKind.Relative), Ct)).StatusCode);

        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/settings/branding/publish");
        var url = (string)(await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/branding"))["images"]!["logoLight"]!;
        using var image = await visitor.GetAsync(new Uri(url, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Png(), await image.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Custom_domains_are_verified_by_dns_unique_and_answer_the_proxys_tls_questions()
    {
        var tenant = await database.CreateTenantAsync("f1dom");
        var other = await database.CreateTenantAsync("f1dom2");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var otherAdmin = await Factory.ForTenant(other).SignedInAsync(database, other, BuiltInRoles.TenantAdmin);
        // The web app asks with the installation's default tenant; any tenant header finds the host's tenant.
        using var anyone = Factory.ForTenant(other);
        var host = $"support-{Guid.NewGuid():N}"[..20] + ".entity.example";

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/settings/branding/domains", new { host = "localhost" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/settings/branding/domains", new { host = "zelvo.example.ae" })).StatusCode);
        var domain = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/settings/branding/domains", new { host = host.ToUpperInvariant() + "." }, HttpStatusCode.Created);
        Assert.Equal(host, (string?)domain["host"]);
        var records = domain["records"]!.AsArray();
        var txt = records.Single(r => (string?)r!["type"] == "TXT")!;
        Assert.Contains(records, r => (string?)r!["type"] == "CNAME" && (string?)r["value"] == "zelvo.example.ae");
        // The same host cannot be claimed by another tenant.
        Assert.Equal(HttpStatusCode.BadRequest, (await otherAdmin.SendAsync(HttpMethod.Post, "/api/v1/settings/branding/domains", new { host })).StatusCode);

        // Not verified yet: unknown to the web app and the proxy.
        var id = (string)domain["id"]!;
        Assert.Equal("txt-missing", (string?)(await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/settings/branding/domains/{id}/verify"))["lastError"]);
        using (var unknown = await anyone.GetAsync(new Uri($"/api/v1/platform/hosts/{host}", UriKind.Relative), Ct))
        {
            Assert.True(unknown.StatusCode == HttpStatusCode.NotFound, await unknown.Content.ReadAsStringAsync(Ct));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync(new Uri($"/internal/tls/ask?domain={host}&token={_tlsToken}", UriKind.Relative), Ct)).StatusCode);

        _dns.Txt[(string)txt["name"]!] = [(string)txt["value"]!];
        Assert.Equal("Verified", (string?)(await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/settings/branding/domains/{id}/verify"))["status"]);
        Assert.Equal(tenant.Slug, (string?)(await (await anyone.GetAsync(new Uri($"/api/v1/platform/hosts/{host}", UriKind.Relative), Ct)).JsonAsync())["tenant"]);
        Assert.Equal(HttpStatusCode.OK, (await anyone.GetAsync(new Uri($"/internal/tls/ask?domain={host}&token={_tlsToken}", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync(new Uri($"/internal/tls/ask?domain={host}&token=wrong-token-value", UriKind.Relative), Ct)).StatusCode);
        Assert.Contains($"https://{host}", Factory.Keycloak.BrandingCalls.Last().Branding.WebUrls);
        // Automatic certificate: the proxy uses its own.
        Assert.Equal(HttpStatusCode.NoContent, (await anyone.GetAsync(new Uri($"/internal/tls/certificate?server_name={host}&token={_tlsToken}", UriKind.Relative), Ct)).StatusCode);

        // The customer's own certificate, made here for the test.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var certificatePem = certificate.ExportCertificatePem();
        var keyPem = key.ExportPkcs8PrivateKeyPem();
        using var otherKey = RSA.Create(2048);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, $"/api/v1/settings/branding/domains/{id}/certificate",
            new { certificatePem, privateKeyPem = otherKey.ExportPkcs8PrivateKeyPem() })).StatusCode);
        var uploaded = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/settings/branding/domains/{id}/certificate", new { certificatePem, privateKeyPem = keyPem });
        Assert.Equal("Uploaded", (string?)uploaded["certificateMode"]);
        Assert.DoesNotContain("PRIVATE KEY", uploaded.ToJsonString(), StringComparison.Ordinal);
        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.DoesNotContain("PRIVATE KEY", (await db.TenantDomains.SingleAsync(Ct)).PrivateKeyCiphertext, StringComparison.Ordinal);
        }

        var served = await (await anyone.GetAsync(new Uri($"/internal/tls/certificate?server_name={host}&token={_tlsToken}", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("BEGIN CERTIFICATE", served, StringComparison.Ordinal);
        Assert.Contains(keyPem.Trim(), served, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sender_domain_gets_a_dkim_key_and_a_guided_dns_check()
    {
        var tenant = await database.CreateTenantAsync("f1mail");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Get, "/api/v1/settings/branding/email")).StatusCode);

        var sender = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/branding/email", new { domain = "Entity.Example", localPart = "Service-Desk" });
        Assert.Equal("service-desk@entity.example", (string?)sender["address"]);
        Assert.False((bool)sender["verified"]!);
        var records = sender["records"]!.AsArray();
        Assert.Equal("v=spf1 include:spf.mail.example.ae ~all", (string?)records[0]!["value"]);

        // Only DMARC published: not verified.
        _dns.Txt["_dmarc.entity.example"] = ["v=DMARC1; p=none"];
        var partly = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/settings/branding/email/check");
        Assert.False((bool)partly["verified"]!);
        Assert.Equal("Missing", (string?)partly["records"]![0]!["status"]);

        _dns.Txt["entity.example"] = ["v=spf1 include:spf.mail.example.ae -all"];
        _dns.Txt[(string)records[1]!["name"]!] = [(string)records[1]!["value"]!];
        var checkedSender = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/settings/branding/email/check");
        Assert.True((bool)checkedSender["verified"]!);
        Assert.Equal("Passed", (string?)checkedSender["records"]![1]!["status"]);

        // The key is kept encrypted and works for signing.
        await using var db = TestData.AppContext(database, tenant);
        var stored = await db.SenderDomains.SingleAsync(Ct);
        Assert.DoesNotContain("PRIVATE KEY", stored.DkimPrivateKeyCiphertext, StringComparison.Ordinal);
    }

    [Fact]
    public void Branded_emails_carry_the_tenants_sender_its_logo_and_a_dkim_signature()
    {
        using var rsa = RSA.Create(2048);
        var message = new EmailMessage("someone@example.org", "Municipality", "Subject", $"<p><img src=\"cid:{NotificationRenderer.LogoContentId}\"></p>", "Text", "en", "id@entity.example")
        {
            FromAddress = "noreply@entity.example",
            Dkim = new DkimKey("entity.example", "zelvo202611", rsa.ExportPkcs8PrivateKeyPem()),
            Images = [new EmailImage(NotificationRenderer.LogoContentId, "image/png", Png())],
        };
        using var mime = SmtpEmailSender.Build(message, "zelvo@installation.example");
        Assert.Equal("noreply@entity.example", ((MailboxAddress)mime.From.Single()).Address);
        Assert.Contains("d=entity.example", mime.Headers[HeaderId.DkimSignature], StringComparison.Ordinal);
        Assert.Contains(mime.BodyParts, p => p.ContentId == NotificationRenderer.LogoContentId);
    }

    [Fact]
    public async Task Pdf_exports_are_branded_made_by_the_worker_and_only_for_the_person_who_asked()
    {
        var tenant = await database.CreateTenantAsync("f1pdf");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = Factory.ForTenant(tenant).As(requesterUser);
        using var someone = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/branding", new { productName = new { en = "City Help", ar = "مساعدة المدينة" } });
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/settings/branding/publish");
        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "طابعة لا تعمل", description = "<script>x</script>", requesterId = requesterUser.Id }));
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/comments", new { body = "Internal detail", visibility = "Internal" }, HttpStatusCode.Created);

        var export = await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/exports", new { language = "ar" }, HttpStatusCode.Accepted);
        Assert.Equal("Pending", (string?)export["status"]);
        Assert.Equal(HttpStatusCode.NotFound, (await someone.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/exports")).StatusCode);

        // The requester's view: branded, right to left, without internal notes, people's text encoded.
        var renderer = new RecordingRenderer();
        var options = new PdfOptions { ChromiumPath = File.Exists("/opt/pw-browsers/chromium") ? "/opt/pw-browsers/chromium" : "chromium" };
        var job = new DocumentExportJob(Factory.Services.GetRequiredService<IServiceScopeFactory>(), renderer, options, NullLogger<DocumentExportJob>.Instance, TimeProvider.System);
        var ran = await job.RunOnceAsync(Ct);
        await using (var check = TestData.AppContext(database, tenant))
        {
            var rows = await check.DocumentExports.AsNoTracking().Select(e => new { e.Status, e.Attempts, e.Error }).ToListAsync(Ct);
            Assert.True(ran == 1, string.Join("; ", rows.Select(r => $"{r.Status} {r.Attempts} {r.Error}")) + " html=" + renderer.Html.Count);
        }

        var html = Assert.Single(renderer.Html);
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("مساعدة المدينة", html, StringComparison.Ordinal);
        Assert.Contains("بتقنية زيلفو", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal detail", html, StringComparison.Ordinal);

        var exportId = (string)export["id"]!;
        Assert.Equal("Ready", (string?)(await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/exports/{exportId}"))["status"]);
        Assert.Equal(HttpStatusCode.NotFound, (await someone.SendAsync(HttpMethod.Get, $"/api/v1/exports/{exportId}/file")).StatusCode);
        using var file = await requester.GetAsync(new Uri($"/api/v1/exports/{exportId}/file", UriKind.Relative), Ct);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType?.MediaType);
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "export.downloaded");

        // Real Chromium, when this computer has it, makes a PDF of the staff view.
        if (File.Exists(options.ChromiumPath))
        {
            await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/exports", new { language = "en" }, HttpStatusCode.Accepted);
            var real = new DocumentExportJob(Factory.Services.GetRequiredService<IServiceScopeFactory>(), new ChromiumPdfRenderer(options), options,
                NullLogger<DocumentExportJob>.Instance, TimeProvider.System);
            Assert.Equal(1, await real.RunOnceAsync(Ct));
            await using var db = TestData.AppContext(database, tenant);
            var made = await db.DocumentExports.AsNoTracking().Where(e => e.Language == "en").SingleAsync(Ct);
            Assert.Equal(ExportStatus.Ready, made.Status);
            Assert.Equal("%PDF-"u8.ToArray(), made.Content![..5]);
        }
    }

    private sealed class RecordingRenderer : IPdfRenderer
    {
        public List<string> Html { get; } = [];

        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken)
        {
            Html.Add(html);
            return Task.FromResult("%PDF-1.7 test"u8.ToArray());
        }
    }
}

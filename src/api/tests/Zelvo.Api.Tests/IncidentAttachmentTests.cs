using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>A scanner that finds the EICAR test string and nothing else.</summary>
internal sealed class FakeScanner : IMalwareScanner
{
    public bool Unavailable { get; set; }

    public Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new MalwareScannerException("down");
        }

        return Task.FromResult(Encoding.ASCII.GetString(content.Span).Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE", StringComparison.Ordinal)
            ? new ScanVerdict(false, "Eicar-Test-Signature")
            : new ScanVerdict(true, null));
    }
}

/// <summary>Attachments: size and type rules, content checks, malware scanning before download, audit (decision 2026-11-22).</summary>
public sealed class IncidentAttachmentTests(DatabaseFixture database) : IAsyncDisposable
{
    // The standard antivirus test file: harmless, and every scanner reports it.
    private const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static MultipartFormDataContent File(string name, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        return form;
    }

    private async Task<(HttpClient Owner, HttpClient Agent, Guid Incident, Data.Tenancy.Tenant Tenant)> IncidentAsync(ApiFactory? factory = null)
    {
        factory ??= _factory;
        var tenant = await database.CreateTenantAsync("att");
        var owner = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var agent = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var incident = IncidentApi.IdOf(await owner.CreateAsync(new { title = "Error on the payment page" }));
        return (owner, agent, incident, tenant);
    }

    [Fact]
    public async Task A_file_cannot_be_downloaded_until_it_is_scanned_clean()
    {
        var (owner, agent, id, tenant) = await IncidentAsync();
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\n%%EOF");

        var upload = await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("screenshot of error.pdf", pdf), Ct);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var attachment = (await upload.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(("Pending", false, "application/pdf"), ((string?)attachment["scanStatus"], (bool?)attachment["downloadable"], (string?)attachment["contentType"]));
        var content = IncidentApi.Of(id, $"attachments/{attachment["id"]}/content");

        var early = await agent.GetAsync(content, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "attachment-not-available"), (early.StatusCode, (await early.ProblemAsync()).Code));

        await using var worker = IncidentApi.Worker(database, new FakeScanner());
        Assert.Equal(1, await worker.GetRequiredService<AttachmentScanJob>().ScanTenantAsync(tenant.Id, tenant.Slug, Ct));

        var download = await agent.GetAsync(content, Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(pdf, await download.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        await using var db = TestData.AppContext(database, tenant);
        var actions = await db.AuditEvents.Where(e => e.EntityType == "ticket" && e.EntityId == id.ToString()).Select(e => e.Action).ToListAsync(Ct);
        Assert.Contains("tickets.attachment.added", actions);
        Assert.Contains("tickets.attachment.downloaded", actions);
    }

    [Fact]
    public async Task An_infected_file_is_rejected_its_content_deleted_and_the_rejection_audited()
    {
        var (owner, _, id, tenant) = await IncidentAsync();
        var upload = await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("notes.txt", Encoding.ASCII.GetBytes(Eicar)), Ct);
        var attachmentId = Guid.Parse((string)(await upload.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!);

        await using var worker = IncidentApi.Worker(database, new FakeScanner());
        await worker.GetRequiredService<AttachmentScanJob>().ScanTenantAsync(tenant.Id, tenant.Slug, Ct);

        var list = await owner.GetFromJsonAsync<JsonArray>(IncidentApi.Of(id, "attachments"), Ct);
        Assert.Equal("Infected", (string?)Assert.Single(list!)!["scanStatus"]);
        var download = await owner.GetAsync(IncidentApi.Of(id, $"attachments/{attachmentId}/content"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);

        await using var db = TestData.AppContext(database, tenant);
        Assert.False(await db.TicketAttachmentContents.AnyAsync(c => c.AttachmentId == attachmentId, Ct));
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "tickets.attachment.infected", Ct);
        Assert.Equal((AuditCategory.Security, AuditActorType.System), (audit.Category, audit.ActorType));
        Assert.Contains("Eicar-Test-Signature", audit.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_the_scanner_is_down_files_stay_unavailable_and_are_tried_again()
    {
        var (owner, _, id, tenant) = await IncidentAsync();
        await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("log.txt", "line 1\n"u8.ToArray()), Ct);
        var scanner = new FakeScanner { Unavailable = true };
        await using var worker = IncidentApi.Worker(database, scanner);
        var job = worker.GetRequiredService<AttachmentScanJob>();

        await Assert.ThrowsAsync<MalwareScannerException>(() => job.ScanTenantAsync(tenant.Id, tenant.Slug, Ct));
        await using var db = TestData.AppContext(database, tenant);
        var attachment = await db.TicketAttachments.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((AttachmentScanStatus.Pending, 1), (attachment.ScanStatus, attachment.ScanAttempts));

        scanner.Unavailable = false;
        Assert.Equal(1, await job.ScanTenantAsync(tenant.Id, tenant.Slug, Ct));
        Assert.Equal(AttachmentScanStatus.Clean, (await db.TicketAttachments.AsNoTracking().SingleAsync(Ct)).ScanStatus);
    }

    [Theory]
    [InlineData("tool.exe", "MZ", "attachment-type-not-allowed")]
    [InlineData("photo.png", "%PDF-1.4", "attachment-content-mismatch")]
    [InlineData("report.pdf", "MZ\u0090\u0000", "attachment-content-mismatch")]
    public async Task Files_of_other_types_or_with_content_that_does_not_match_are_refused(string name, string content, string code)
    {
        var (owner, _, id, _) = await IncidentAsync();

        var response = await owner.PostAsync(IncidentApi.Of(id, "attachments"), File(name, Encoding.Latin1.GetBytes(content)), Ct);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, code), (response.StatusCode, (await response.ProblemAsync()).Code));
    }

    [Fact]
    public async Task The_tenant_sets_the_largest_file_and_the_accepted_types()
    {
        var (owner, _, id, tenant) = await IncidentAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var uri = new Uri("/api/v1/settings/attachments", UriKind.Relative);
        var defaults = await admin.GetFromJsonAsync<JsonObject>(uri, Ct);
        Assert.Equal((25, true), ((int?)defaults!["maxSizeMb"], (bool?)defaults["scanningEnabled"]));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(uri, new { maxSizeMb = 1, allowedExtensions = new[] { "exe" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(uri, new { maxSizeMb = 1, allowedExtensions = new[] { "txt" } }, Ct)).StatusCode);

        var large = await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("big.txt", new byte[(1024 * 1024) + 1].Select(_ => (byte)'a').ToArray()), Ct);
        Assert.Equal("attachment-too-large", (await large.ProblemAsync()).Code);
        var pdf = await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("a.pdf", "%PDF-1.7"u8.ToArray()), Ct);
        Assert.Equal("attachment-type-not-allowed", (await pdf.ProblemAsync()).Code);
    }

    [Fact]
    public async Task Others_cannot_see_attach_or_remove_files_and_closed_incidents_take_no_more_files()
    {
        var (owner, agent, id, tenant) = await IncidentAsync();
        using var stranger = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var upload = await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("a.txt", "hello"u8.ToArray()), Ct);
        var attachmentId = (string)(await upload.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!;

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(IncidentApi.Of(id, "attachments"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync(IncidentApi.Of(id, "attachments"), File("b.txt", "x"u8.ToArray()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync(IncidentApi.Of(id, $"attachments/{attachmentId}"), Ct)).StatusCode);

        (await agent.MoveAsync(id, "Resolved", notes: "Done")).EnsureSuccessStatusCode();
        (await agent.MoveAsync(id, "Closed")).EnsureSuccessStatusCode();
        Assert.Equal("ticket-closed", (await (await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("c.txt", "x"u8.ToArray()), Ct)).ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(IncidentApi.Of(id, $"attachments/{attachmentId}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_uploader_removes_their_file_and_its_content()
    {
        var (owner, _, id, tenant) = await IncidentAsync();
        var upload = await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("a.txt", "hello"u8.ToArray()), Ct);
        var attachmentId = Guid.Parse((string)(await upload.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(IncidentApi.Of(id, $"attachments/{attachmentId}"), Ct)).StatusCode);

        await using var db = TestData.AppContext(database, tenant);
        Assert.False(await db.TicketAttachments.AnyAsync(Ct));
        Assert.False(await db.TicketAttachmentContents.AnyAsync(Ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "tickets.attachment.removed", Ct));
    }

    [Fact]
    public async Task With_scanning_turned_off_files_are_marked_not_scanned_and_can_be_downloaded()
    {
        await using var factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");
        var (owner, _, id, _) = await IncidentAsync(factory);

        var upload = await owner.PostAsync(IncidentApi.Of(id, "attachments"), File("a.txt", "hello"u8.ToArray()), Ct);
        var attachment = (await upload.Content.ReadFromJsonAsync<JsonObject>(Ct))!;

        Assert.Equal(("NotScanned", true), ((string?)attachment["scanStatus"], (bool?)attachment["downloadable"]));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(IncidentApi.Of(id, $"attachments/{attachment["id"]}/content"), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_ClamAV_client_streams_the_file_in_chunks_and_reads_the_verdict()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var received = new MemoryStream();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            await using var stream = client.GetStream();
            var command = new byte[10];
            await stream.ReadExactlyAsync(command, Ct);
            Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));
            var length = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(length, Ct);
                var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(length);
                if (size == 0)
                {
                    break;
                }

                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk, Ct);
                received.Write(chunk);
            }

            await stream.WriteAsync("stream: Eicar-Signature FOUND\0"u8.ToArray(), Ct);
        }, Ct);

        var scanner = new ClamAvScanner(Options.Create(new AttachmentScanningOptions { Host = "127.0.0.1", Port = port }));
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var verdict = await scanner.ScanAsync(content, Ct);
        await server;

        Assert.Equal(new ScanVerdict(false, "Eicar-Signature"), verdict);
        Assert.Equal(content, received.ToArray());
    }

    [Fact]
    public async Task An_unreachable_scanner_is_reported_not_taken_as_clean()
    {
        var scanner = new ClamAvScanner(Options.Create(new AttachmentScanningOptions { Host = "127.0.0.1", Port = 1, Timeout = TimeSpan.FromSeconds(2) }));

        await Assert.ThrowsAsync<MalwareScannerException>(() => scanner.ScanAsync("x"u8.ToArray(), Ct));
    }
}

/// <summary>Against a real ClamAV daemon; runs when ZELVO_TEST_CLAMAV names one (host:port), for example the Compose service.</summary>
public sealed class RealClamAvTests
{
    [Fact]
    public async Task ClamAV_finds_the_antivirus_test_file_and_passes_a_clean_file()
    {
        var address = Environment.GetEnvironmentVariable("ZELVO_TEST_CLAMAV");
        Assert.SkipWhen(string.IsNullOrEmpty(address), "Set ZELVO_TEST_CLAMAV=host:port to run against a real ClamAV.");
        var (host, port) = (address!.Split(':')[0], int.Parse(address.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture));
        var scanner = new ClamAvScanner(Options.Create(new AttachmentScanningOptions { Host = host, Port = port }));
        const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

        var infected = await scanner.ScanAsync(Encoding.ASCII.GetBytes(Eicar), TestContext.Current.CancellationToken);
        var clean = await scanner.ScanAsync(new byte[30 * 1024 * 1024], TestContext.Current.CancellationToken);

        Assert.False(infected.Clean);
        Assert.Contains("Eicar", infected.Signature, StringComparison.OrdinalIgnoreCase);
        Assert.True(clean.Clean);
    }

    [Fact]
    public async Task ClamAV_says_which_definitions_it_has_loaded()
    {
        var address = Environment.GetEnvironmentVariable("ZELVO_TEST_CLAMAV");
        Assert.SkipWhen(string.IsNullOrEmpty(address), "Set ZELVO_TEST_CLAMAV=host:port to run against a real ClamAV.");
        var (host, port) = (address!.Split(':')[0], int.Parse(address.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture));
        var client = new Zelvo.Tickets.Malware.ClamAvVersionClient(Options.Create(new AttachmentScanningOptions { Host = host, Port = port }));

        var loaded = await client.GetAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("1.", loaded.Engine, StringComparison.Ordinal);
        Assert.True(loaded.DailyVersion > 27000);
        Assert.True(loaded.DailyBuiltAt > new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}

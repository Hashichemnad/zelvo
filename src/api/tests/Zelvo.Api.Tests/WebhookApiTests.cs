using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Zelvo.Api.Tests;

/// <summary>Outgoing webhooks (module 14): signed payloads, retries, delivery log, tester, address policy.</summary>
public sealed class WebhookApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri Webhooks = new("/api/v1/integrations/webhooks", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithLocalWebhooks(retries: 2, disableAfter: 2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Receiver_addresses_must_be_https_and_outside_internal_networks()
    {
        var tenant = await database.CreateTenantAsync("wu");
        await using var strict = ApiFactory.ForDatabase(database).WithMasterKey();
        using var admin = await strict.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");

        foreach (var (url, code) in new[]
                 {
                     ("http://hooks.example.ae/z", "url-not-https"),
                     ("https://127.0.0.1/z", "address-not-allowed"),
                     ("https://169.254.169.254/latest", "address-not-allowed"),
                     ("https://localhost/z", "address-not-allowed"),
                     ("https://user:pw@hooks.example.ae/z", "url-invalid"),
                 })
        {
            using var response = await admin.PostAsJsonAsync(Webhooks, new { name = "x", url, events = new[] { "user.created" } }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(code, (string?)(await response.JsonAsync())["urlProblem"]);
        }

        using var unknownEvent = await admin.PostAsJsonAsync(Webhooks, new { name = "x", url = "https://hooks.example.ae/z", events = new[] { "ticket.eaten" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknownEvent.StatusCode);
        using var testEvent = await admin.PostAsJsonAsync(Webhooks, new { name = "x", url = "https://hooks.example.ae/z", events = new[] { "webhook.test" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, testEvent.StatusCode);
        using var ok = await admin.PostAsJsonAsync(Webhooks, new { name = "x", url = "https://hooks.example.ae/z", events = new[] { "user.created" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task The_tester_sends_a_signed_test_event_now_and_shows_the_answer()
    {
        var tenant = await database.CreateTenantAsync("wt");
        await using var receiver = await TestWebhookReceiver.StartAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (id, secret) = await SubscribeAsync(admin, receiver, "user.created");

        using var test = await admin.PostAsync(new Uri($"/api/v1/integrations/webhooks/{id}/test", UriKind.Relative), null, Ct);
        var result = await test.JsonAsync();

        Assert.True((bool)result["succeeded"]!);
        Assert.Equal(200, (int?)result["statusCode"]);
        Assert.Contains("[masked]", (string?)result["responseBody"], StringComparison.Ordinal);
        var received = Assert.Single(receiver.Received);
        Assert.True(received.SignedWith(secret));
        Assert.Equal("webhook.test", received.Event);
        Assert.Equal("development", received.Environment);
        Assert.Equal("webhook.test", (string?)received.Json["type"]);
        Assert.Equal(tenant.Slug, (string?)received.Json["tenant"]);
        Assert.Equal("msg_" + Guid.Parse((string)received.Json["id"]!).ToString("N"), received.Id);

        var deliveries = await (await admin.GetAsync(new Uri($"/api/v1/integrations/webhooks/{id}/deliveries", UriKind.Relative), Ct)).JsonAsync();
        var delivery = deliveries["items"]![0]!;
        Assert.Equal("Succeeded", (string?)delivery["status"]);
        Assert.True((bool)delivery["manual"]!);

        var log = await (await admin.GetAsync(new Uri("/api/v1/integrations/log?direction=Outgoing", UriKind.Relative), Ct)).JsonAsync();
        Assert.Equal("POST", (string?)log["items"]![0]!["method"]);
        Assert.Equal(receiver.Url.ToString(), (string?)log["items"]![0]!["target"]);
    }

    [Fact]
    public async Task User_changes_are_delivered_by_the_worker_signed_and_without_personal_data()
    {
        var tenant = await database.CreateTenantAsync("we");
        await using var receiver = await TestWebhookReceiver.StartAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (id, secret) = await SubscribeAsync(admin, receiver, "user.created", "user.updated", "user.disabled", "role.updated");

        var agent = await database.CreateUserAsync(tenant, "agent");
        using var first = _factory.ForTenant(tenant).As(agent);
        await first.GetAsync(new Uri("/api/v1/tenant", UriKind.Relative), Ct);
        using var patch = await admin.PatchAsJsonAsync(new Uri($"/api/v1/users/{agent.Id}", UriKind.Relative), new { displayName = "Renamed", status = "Disabled" }, Ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var roles = await admin.PutAsJsonAsync(new Uri($"/api/v1/users/{agent.Id}/roles", UriKind.Relative), new { roles = new[] { "agent", "approver" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);

        // Queued in the same transaction as the change; nothing is sent before the worker runs.
        Assert.Empty(receiver.Received);
        Assert.True(await _factory.DeliverWebhooksAsync() >= 3);

        var events = receiver.Received.Select(r => r.Json).ToList();
        Assert.All(receiver.Received, r => Assert.True(r.SignedWith(secret)));
        Assert.Contains(events, e => (string?)e["type"] == "user.disabled" && (string?)e["data"]!["id"] == agent.Id.ToString());
        var updates = events.Where(e => (string?)e["type"] == "user.updated").ToList();
        Assert.Contains(updates, e => e["data"]!["changed"]!.AsArray().Select(f => (string?)f).SequenceEqual(["displayName"]));
        Assert.Contains(updates, e => e["data"]!["changed"]!.AsArray().Select(f => (string?)f).SequenceEqual(["roles"]));
        Assert.All(receiver.Received, r =>
        {
            Assert.DoesNotContain(agent.Email, r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Renamed", r.Body, StringComparison.Ordinal);
        });

        var deliveries = await (await admin.GetAsync(new Uri($"/api/v1/integrations/webhooks/{id}/deliveries?status=Succeeded", UriKind.Relative), Ct)).JsonAsync();
        Assert.Equal(receiver.Received.Count, (int?)deliveries["totalCount"]);
        Assert.Equal(0, await _factory.DeliverWebhooksAsync());
    }

    [Fact]
    public async Task Failed_deliveries_are_retried_then_given_up_and_repeated_failures_disable_the_subscription()
    {
        var tenant = await database.CreateTenantAsync("wf");
        await using var receiver = await TestWebhookReceiver.StartAsync();
        receiver.Status = 503;
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (id, _) = await SubscribeAsync(admin, receiver, "role.updated");

        for (var i = 0; i < 2; i++)
        {
            using var change = await admin.PatchAsJsonAsync(new Uri("/api/v1/roles/approver", UriKind.Relative), RoleChange(i), Ct);
            Assert.True(change.IsSuccessStatusCode, await change.Content.ReadAsStringAsync(Ct));
        }

        // Two retries with no wait (test settings): three attempts each, then given up.
        for (var i = 0; i < 3; i++)
        {
            await _factory.DeliverWebhooksAsync();
        }

        Assert.Equal(6, receiver.Received.Count);
        var failed = await (await admin.GetAsync(new Uri($"/api/v1/integrations/webhooks/{id}/deliveries", UriKind.Relative), Ct)).JsonAsync();
        Assert.All(failed["items"]!.AsArray(), d =>
        {
            Assert.Equal("Failed", (string?)d!["status"]);
            Assert.Equal(3, (int?)d["attempts"]);
            Assert.StartsWith("http-503", (string?)d["lastError"], StringComparison.Ordinal);
        });

        var subscription = await (await admin.GetAsync(new Uri($"/api/v1/integrations/webhooks/{id}", UriKind.Relative), Ct)).JsonAsync();
        Assert.Equal("Disabled", (string?)subscription["status"]);
        Assert.StartsWith("too-many-failures", (string?)subscription["disabledReason"], StringComparison.Ordinal);
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "integrations.webhook.disabled" && a.ActorType == "System");

        // Disabled: no new deliveries. Turned on again: delivered once the receiver is back.
        using (var change = await admin.PatchAsJsonAsync(new Uri("/api/v1/roles/approver", UriKind.Relative), RoleChange(5), Ct))
        {
            Assert.True(change.IsSuccessStatusCode);
        }

        Assert.Equal(0, await _factory.DeliverWebhooksAsync());
        receiver.Status = 200;
        using var resume = await admin.PatchAsJsonAsync(new Uri($"/api/v1/integrations/webhooks/{id}", UriKind.Relative), new { status = "Active" }, Ct);
        Assert.Equal(0, (int?)(await resume.JsonAsync())["consecutiveFailures"]);

        var lastId = (long)failed["items"]![0]!["id"]!;
        using var redeliver = await admin.PostAsync(new Uri($"/api/v1/integrations/webhooks/{id}/deliveries/{lastId}/redeliver", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, redeliver.StatusCode);
        Assert.Equal(1, await _factory.DeliverWebhooksAsync());
        var redelivered = receiver.Received.Last();
        Assert.Equal(receiver.Received.First(r => r.Id == redelivered.Id).Body, redelivered.Body);
    }

    [Fact]
    public async Task During_a_secret_rotation_webhooks_carry_signatures_for_both_secrets()
    {
        var tenant = await database.CreateTenantAsync("wr");
        await using var receiver = await TestWebhookReceiver.StartAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (id, oldSecret) = await SubscribeAsync(admin, receiver, "user.created");

        using var rotate = await admin.PostAsync(new Uri($"/api/v1/integrations/webhooks/{id}/rotate-secret", UriKind.Relative), null, Ct);
        var body = await rotate.JsonAsync();
        var newSecret = (string)body["secret"]!;
        Assert.True((bool)body["webhook"]!["secretRotating"]!);

        await admin.PostAsync(new Uri($"/api/v1/integrations/webhooks/{id}/test", UriKind.Relative), null, Ct);
        var received = Assert.Single(receiver.Received);
        Assert.True(received.SignedWith(newSecret));
        Assert.True(received.SignedWith(oldSecret));

        var stored = await database.ScalarAsync<string>("SELECT secret_ciphertext FROM webhook_subscriptions WHERE id = @id", ("id", id));
        Assert.StartsWith("zv1.", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(newSecret, stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paused_subscriptions_get_no_events_and_deleting_removes_the_delivery_log()
    {
        var tenant = await database.CreateTenantAsync("wp");
        await using var receiver = await TestWebhookReceiver.StartAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (id, _) = await SubscribeAsync(admin, receiver, "role.updated");

        await admin.PatchAsJsonAsync(new Uri($"/api/v1/integrations/webhooks/{id}", UriKind.Relative), new { status = "Paused" }, Ct);
        using (var change = await admin.PatchAsJsonAsync(new Uri("/api/v1/roles/approver", UriKind.Relative), RoleChange(1), Ct))
        {
            Assert.True(change.IsSuccessStatusCode);
        }

        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM webhook_deliveries WHERE subscription_id = @id", ("id", id)));
        using var disable = await admin.PatchAsJsonAsync(new Uri($"/api/v1/integrations/webhooks/{id}", UriKind.Relative), new { status = "Disabled" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);

        await admin.PostAsync(new Uri($"/api/v1/integrations/webhooks/{id}/test", UriKind.Relative), null, Ct);
        Assert.Single(receiver.Received);
        using var delete = await admin.DeleteAsync(new Uri($"/api/v1/integrations/webhooks/{id}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM webhook_deliveries WHERE subscription_id = @id", ("id", id)));
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "integrations.webhook.deleted");
    }

    [Fact]
    public async Task Webhooks_are_never_sent_to_internal_addresses_even_when_a_saved_url_resolves_to_one()
    {
        var tenant = await database.CreateTenantAsync("wi");
        await using var strict = ApiFactory.ForDatabase(database).WithMasterKey().WithSetting("Integrations:Webhooks:AllowInsecureHttp", "true");
        using var admin = await strict.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        using var created = await admin.PostAsJsonAsync(Webhooks, new { name = "x", url = "http://hooks.example.ae/z", events = new[] { "user.created" } }, Ct);
        var id = (string)(await created.JsonAsync())["webhook"]!["id"]!;

        // As if DNS now pointed the name at this server.
        await database.ExecuteAsOwnerAsync("UPDATE webhook_subscriptions SET url = 'http://localhost:9/z' WHERE id = @id", ("id", Guid.Parse(id)));
        using var test = await admin.PostAsync(new Uri($"/api/v1/integrations/webhooks/{id}/test", UriKind.Relative), null, Ct);
        var result = await test.JsonAsync();

        Assert.False((bool)result["succeeded"]!);
        Assert.StartsWith("address-not-allowed", (string?)result["error"], StringComparison.Ordinal);
    }

    private static object RoleChange(int i) => new { name = new { en = $"Approver {i}", ar = $"المعتمِد {i}" } };

    private static async Task<(Guid Id, string Secret)> SubscribeAsync(HttpClient admin, TestWebhookReceiver receiver, params string[] events)
    {
        using var response = await admin.PostAsJsonAsync(Webhooks, new { name = "Receiver", url = receiver.Url.ToString(), events }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        return (Guid.Parse((string)body["webhook"]!["id"]!), (string)body["secret"]!);
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Api.Payments;
using Zelvo.Data.Identity;
using Zelvo.Data.Payments;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;

namespace Zelvo.Api.Tests;

/// <summary>
/// A payment gateway in memory: creates payments, answers its status API from what the test set,
/// and refunds. Addresses are https://gateway.test/…; nothing leaves the process.
/// </summary>
internal sealed class FakeGateway : HttpMessageHandler
{
    public sealed record Transaction(string Id, string Reference, decimal Amount)
    {
        public string Status { get; set; } = "PENDING";

        public decimal? ReportedAmount { get; set; }
    }

    public ConcurrentDictionary<string, Transaction> Transactions { get; } = new();

    public List<string> Calls { get; } = [];

    public Transaction ByReference(string reference) => Transactions.Values.Single(t => t.Reference == reference);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (Calls)
        {
            Calls.Add($"{request.Method} {path} {string.Join(",", request.Headers.Select(h => h.Key))}");
        }

        if (request.Method == HttpMethod.Post && path == "/payments")
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var id = "T" + Guid.NewGuid().ToString("N")[..10];
            Transactions[id] = new Transaction(id, (string)body["order"]!["ref"]!, (decimal)body["amount"]!);
            return Json(new JsonObject { ["data"] = new JsonObject { ["id"] = id, ["url"] = $"https://pay.gateway.test/checkout/{id}" } });
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/payments/", StringComparison.Ordinal) && Transactions.TryGetValue(path[10..], out var t))
        {
            return Json(new JsonObject { ["status"] = t.Status, ["amount"] = t.ReportedAmount ?? t.Amount, ["ref"] = t.Reference });
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/refunds", StringComparison.Ordinal))
        {
            return Json(new JsonObject { ["refundId"] = "R1", ["status"] = "REFUNDED" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    }

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

/// <summary>Online payments (task E8, module 15) against PostgreSQL with a fake gateway.</summary>
public sealed class PaymentApiTests : IAsyncDisposable
{
    private readonly DatabaseFixture _database;
    private readonly FakeGateway _gateway = new();
    private readonly ApiFactory _factory;

    public PaymentApiTests(DatabaseFixture database)
    {
        _database = database;
        _factory = ApiFactory.ForDatabase(database).WithMasterKey().WithSetting("AttachmentScanning:Enabled", "false")
            .WithSetting("Payments:PublicUrl", "https://portal.example.test/")
            .WithServices(services =>
            {
                services.RemoveAll<PaymentGatewayClient>();
                services.AddSingleton(_ => new PaymentGatewayClient(new OutboundAddressPolicy(new OutboundNetworkOptions()), _gateway));
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync()
    {
        _gateway.Dispose();
        return _factory.DisposeAsync();
    }

    private static JsonObject Text(string en) => new() { ["en"] = en, ["ar"] = en + " (ar)" };

    /// <summary>A connector for the fake gateway, mapped as an administrator would from its documentation.</summary>
    private static JsonObject Connector(bool signedCallback = true, bool refunds = true)
    {
        var connector = new JsonObject
        {
            ["create"] = new JsonObject
            {
                ["method"] = "POST", ["url"] = "https://gateway.test/payments", ["bodyFormat"] = "Json",
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer {{secret.apiKey}}" },
                ["body"] = new JsonObject
                {
                    ["amount"] = "{{amount}}", ["currency"] = "{{currency}}", ["order.ref"] = "{{reference}}", ["merchant"] = "{{merchantId}}",
                    ["returnUrl"] = "{{returnUrl}}", ["notifyUrl"] = "{{callbackUrl}}",
                },
                ["response"] = new JsonObject { ["transactionId"] = "data.id", ["redirectUrl"] = "data.url" },
            },
            ["status"] = new JsonObject
            {
                ["method"] = "GET", ["url"] = "https://gateway.test/payments/{{transactionId}}", ["bodyFormat"] = "None",
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer {{secret.apiKey}}" },
                ["response"] = new JsonObject { ["status"] = "status", ["amount"] = "amount", ["reference"] = "ref" },
            },
            ["statusValues"] = new JsonObject { ["paid"] = new JsonArray("CAPTURED"), ["failed"] = new JsonArray("DECLINED"), ["cancelled"] = new JsonArray("CANCELLED") },
            ["callback"] = new JsonObject
            {
                ["referenceField"] = "ref",
                ["signature"] = signedCallback ? new JsonObject { ["algorithm"] = "HmacSha256", ["secretName"] = "webhookKey", ["header"] = "X-Signature" } : new JsonObject(),
            },
            ["settlement"] = new JsonObject { ["reference"] = "Merchant Ref", ["amount"] = "Amount", ["transactionId"] = "Txn" },
        };
        if (refunds)
        {
            connector["refund"] = new JsonObject
            {
                ["method"] = "POST", ["url"] = "https://gateway.test/payments/{{transactionId}}/refunds", ["bodyFormat"] = "Json",
                ["body"] = new JsonObject { ["amount"] = "{{refundAmount}}" },
                ["response"] = new JsonObject { ["refundId"] = "refundId", ["status"] = "status" },
            };
            connector["refundSucceeded"] = new JsonArray("REFUNDED");
        }

        return connector;
    }

    private sealed record Setup(Tenant Tenant, HttpClient Admin, HttpClient Publisher, Guid GatewayId, string WebhookKey);

    private async Task<Setup> SetUpAsync(string slug)
    {
        var tenant = await _database.CreateTenantAsync(slug);
        var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var publisher = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var webhookKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var gateway = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/payments/gateways", new
        {
            name = "Test pay", merchantId = "M-1", connector = Connector(),
            credentials = new Dictionary<string, string> { ["apiKey"] = Guid.NewGuid().ToString("N"), ["webhookKey"] = webhookKey },
        }, HttpStatusCode.Created);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/payments", new { enabled = true, unpaidExpiryHours = 24 });
        return new Setup(tenant, admin, publisher, gateway.Id(), webhookKey);
    }

    private static async Task<Guid> ServiceAsync(HttpClient author, HttpClient reviewer, string code, JsonObject card)
    {
        var id = (await author.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code }, HttpStatusCode.Created)).Id();
        var draft = await author.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{id}/draft", new { card });
        Assert.Empty(draft["problems"]!.AsArray());
        await author.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{id}/submit");
        await reviewer.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{id}/approve");
        return id;
    }

    private async Task<HttpResponseMessage> CallbackAsync(Tenant tenant, string reference, string? key)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["ref"] = reference, ["status"] = "CAPTURED" }.ToJsonString());
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Zelvo-Tenant", tenant.Slug);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (key is not null)
        {
            content.Headers.Add("X-Signature", Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body)));
        }

        return await client.PostAsync(new Uri($"/api/v1/payments/callback/{reference}", UriKind.Relative), content, Ct);
    }

    [Fact]
    public async Task Gateways_keep_their_credentials_secret_and_go_live_only_after_a_sandbox_test_payment()
    {
        var tenant = await _database.CreateTenantAsync("pg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);

        // Payments cannot be turned on without a gateway; a broken connector is refused.
        using (var none = await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/payments", new { enabled = true }))
        {
            Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        }

        var broken = Connector();
        broken["create"]!["url"] = "http://10.0.0.5/payments";
        broken["create"]!["headers"]!["X-Key"] = "{{secret.missing}} {{nope}}";
        using (var bad = await admin.SendAsync(HttpMethod.Post, "/api/v1/payments/gateways", new { name = "Bad", connector = broken }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var codes = (await bad.Content.ReadFromJsonAsync<JsonObject>(Ct))!["problems"]!.AsArray().Select(p => (string?)p!["code"]).ToList();
            Assert.Contains("url-invalid", codes);
            Assert.Contains("credential-unknown", codes);
            Assert.Contains("placeholder-unknown", codes);
        }

        var apiKey = Guid.NewGuid().ToString("N");
        var gateway = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/payments/gateways", new
        {
            name = "AjmanPay sandbox", connector = Connector(), credentials = new Dictionary<string, string> { ["apiKey"] = apiKey, ["webhookKey"] = "k" },
        }, HttpStatusCode.Created);
        var id = gateway.Id();
        Assert.DoesNotContain(apiKey, gateway.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(["apiKey", "webhookKey"], gateway["credentialNames"]!.AsArray().Select(n => (string?)n));
        Assert.True((bool)gateway["isDefault"]!);

        // A new host needs the credentials again.
        var moved = Connector();
        moved["create"]!["url"] = "https://other.test/payments";
        using (var again = await admin.SendAsync(HttpMethod.Put, $"/api/v1/payments/gateways/{id}", new { connector = moved }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        }

        var test = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/gateways/{id}/test-connection");
        Assert.True((bool)test["reachable"]!);

        using (var live = await admin.SendAsync(HttpMethod.Put, $"/api/v1/payments/gateways/{id}", new { mode = "Live" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, live.StatusCode);
        }

        var pay = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/gateways/{id}/test-payment");
        Assert.StartsWith("https://pay.gateway.test/checkout/", (string?)pay["redirectUrl"], StringComparison.Ordinal);
        var reference = (string)pay["reference"]!;
        _gateway.ByReference(reference).Status = "CAPTURED";
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/attempts/{reference}/confirm");
        var wentLive = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/payments/gateways/{id}", new { mode = "Live" });
        Assert.Equal("Live", (string?)wentLive["mode"]);

        // The secret went to the gateway in the header the connector names.
        Assert.Contains(_gateway.Calls, c => c.StartsWith("POST /payments ", StringComparison.Ordinal) && c.Contains("Authorization", StringComparison.Ordinal));

        await using var db = TestData.AppContext(_database, tenant);
        var stored = await db.PaymentGateways.AsNoTracking().SingleAsync(g => g.Id == id, Ct);
        Assert.DoesNotContain(apiKey, stored.EncryptedCredentials, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, string.Join(" ", await db.AuditEvents.AsNoTracking().Select(e => e.Metadata).ToListAsync(Ct)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fee_due_on_submission_holds_the_request_until_the_gateways_status_api_confirms_it_once()
    {
        var s = await SetUpAsync("pp");
        using var admin = s.Admin;
        using var publisher = s.Publisher;
        var requesterUser = await _database.CreateUserAsync(s.Tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(s.Tenant).As(requesterUser);
        var group = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = Text("Licensing") }, HttpStatusCode.Created)).Id();
        var form = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms", new
        {
            key = "urgency", name = new { en = "Urgency" },
            schema = new { elements = new[] { new { type = "Field", field = new { key = "urgent", type = "Checkbox", label = new { en = "Urgent", ar = "عاجل" } } } } },
        }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{form}/publish");
        var service = await ServiceAsync(admin, publisher, "LIC-NEW", new JsonObject
        {
            ["name"] = Text("Trade licence"), ["channels"] = new JsonArray("Portal"), ["formId"] = form.ToString(),
            ["fulfilment"] = new JsonObject { ["groupId"] = group.ToString() },
            ["fee"] = new JsonObject
            {
                ["paid"] = true, ["amountAed"] = 100, ["timing"] = "OnSubmission",
                ["rules"] = new JsonArray(new JsonObject
                {
                    ["name"] = Text("Urgent delivery"), ["amountAed"] = 50.5,
                    ["conditions"] = new JsonArray(new JsonObject { ["field"] = "answer.urgent", ["op"] = "eq", ["value"] = true }),
                }),
            },
        });
        var version = (string?)(await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/catalog/published/{service}/request-form"))["form"]!["versionId"];
        var created = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests",
            new { serviceId = service, formVersionId = version, formAnswers = new { urgent = true } }, HttpStatusCode.Created);
        var id = created.Id();
        Assert.Equal((true, "Payment"), ((bool)created["held"]!, (string?)created["hold"]));
        Assert.Null(created["group"]);

        var payment = (await requester.ArrayAsync($"/api/v1/service-requests/{id}/payments")).Single()!.AsObject();
        Assert.Equal((150.5m, "AwaitingPayment"), ((decimal)payment["amountAed"]!, (string?)payment["state"]));
        Assert.Equal(2, payment["lines"]!.AsArray().Count);
        var paymentId = Guid.Parse((string)payment["id"]!);

        // Someone else cannot pay or see it.
        using var stranger = await _factory.ForTenant(s.Tenant).SignedInAsync(_database, s.Tenant, BuiltInRoles.Requester);
        using (var denied = await stranger.SendAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/pay", new { language = "en" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }

        var pay = await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/pay", new { language = "ar" });
        var reference = (string)pay["reference"]!;
        var transaction = _gateway.ByReference(reference);
        Assert.Equal(150.5m, transaction.Amount);

        // Coming back before it is paid changes nothing; a wrong amount is not accepted.
        Assert.Equal("AwaitingPayment", (string?)(await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/attempts/{reference}/confirm"))["state"]);
        transaction.Status = "CAPTURED";
        transaction.ReportedAmount = 1m;
        Assert.Equal("AwaitingPayment", (string?)(await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/attempts/{reference}/confirm"))["state"]);
        transaction.ReportedAmount = null;

        // A callback with a wrong signature is ignored; a signed one is confirmed with the status API, once.
        using (var forged = await CallbackAsync(s.Tenant, reference, "not-the-key"))
        {
            Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        }

        Assert.Equal("AwaitingPayment", (string?)(await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}"))["state"]);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            using var signed = await CallbackAsync(s.Tenant, reference, s.WebhookKey);
            Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        }

        var paid = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}");
        Assert.Equal(("Paid", "RCT-000001", transaction.Id), ((string?)paid["state"], (string?)paid["receiptNumber"], (string?)paid["transactionId"]));
        var request = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.Equal((false, group.ToString()), ((bool)request["held"]!, (string?)request["group"]!["id"]));
        var receipt = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}/receipt");
        Assert.Equal(("RCT-000001", 150.5m), ((string?)receipt["receiptNumber"], (decimal)receipt["amountAed"]!));

        await using (var db = TestData.AppContext(_database, s.Tenant))
        {
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.EventType == "payment.requested" && n.RecipientId == requesterUser.Id, Ct));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.EventType == "payment.received" && n.RecipientId == requesterUser.Id, Ct));
            Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "payments.paid", Ct));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "payments.callback.rejected", Ct));
        }

        // Refunds: part through the gateway's refund API, the rest when the gateway has none is manual.
        using var finance = await _factory.ForTenant(s.Tenant).SignedInAsync(_database, s.Tenant, BuiltInRoles.ExecutiveAuditor);
        using (var noRight = await finance.SendAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/refunds", new { amountAed = 10, reason = "Overcharged" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noRight.StatusCode);
        }

        var refunded = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/refunds", new { amountAed = 50.5, reason = "Not urgent after all" });
        Assert.Equal(("PartlyRefunded", "Completed"), ((string?)refunded["state"], (string?)refunded["refunds"]![0]!["state"]));
        using (var tooMuch = await admin.SendAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/refunds", new { amountAed = 1000, reason = "x" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        }

        // Finance: list, Excel export and the day's reconciliation.
        var list = await finance.SendOkAsync(HttpMethod.Get, "/api/v1/payments");
        Assert.Single(list["items"]!.AsArray());
        using (var export = await finance.SendAsync(HttpMethod.Get, "/api/v1/payments/export?format=xlsx"))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var day = await finance.SendOkAsync(HttpMethod.Post, "/api/v1/payments/reconciliations", new { gatewayId = s.GatewayId, day = today });
        Assert.Equal((1, 0), ((int)day["matched"]!, (int)day["mismatched"]!));
        using var file = new MultipartFormDataContent();
        using var csv = new ByteArrayContent(Encoding.UTF8.GetBytes($"Merchant Ref,Txn,Amount\n{reference},{transaction.Id},150.50\nZPUNKNOWN,T9,20.00\n"));
        file.Add(csv, "file", "settlement.csv");
        using (var imported = await finance.PostAsync(new Uri($"/api/v1/payments/reconciliations/{(string)day["id"]!}/settlement-file", UriKind.Relative), file, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            var body = (await imported.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
            Assert.Equal((1, 1), ((int)body["matched"]!, (int)body["unknownToZelvo"]!));
        }
    }

    [Fact]
    public async Task Unpaid_requests_close_after_their_time_and_a_fee_due_on_completion_must_be_paid_before_closing()
    {
        var s = await SetUpAsync("pe");
        using var admin = s.Admin;
        using var publisher = s.Publisher;
        var requesterUser = await _database.CreateUserAsync(s.Tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(s.Tenant).As(requesterUser);
        var upfront = await ServiceAsync(admin, publisher, "LIC-COPY", new JsonObject
        {
            ["name"] = Text("Licence copy"), ["channels"] = new JsonArray("Portal"), ["fee"] = new JsonObject { ["paid"] = true, ["amountAed"] = 20 },
        });
        var later = await ServiceAsync(admin, publisher, "FAC-HALL", new JsonObject
        {
            ["name"] = Text("Hall booking"), ["channels"] = new JsonArray("Portal"), ["fee"] = new JsonObject { ["paid"] = true, ["amountAed"] = 300, ["timing"] = "OnCompletion" },
        });

        var unpaid = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = upfront }, HttpStatusCode.Created)).Id();
        await _factory.Services.GetRequiredService<PaymentJobs>().RunTenantAsync(s.Tenant.Id, s.Tenant.Slug, DateTimeOffset.UtcNow.AddHours(25), Ct);
        var closed = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{unpaid}");
        Assert.Equal("Closed", (string?)closed["state"]);
        Assert.Contains("not paid", (string?)closed["resolutionNotes"], StringComparison.Ordinal);
        Assert.Equal("Expired", (string?)(await requester.ArrayAsync($"/api/v1/service-requests/{unpaid}/payments")).Single()!["state"]);

        // On completion: no hold; once resolved, the fee is asked for and the request closes only once paid.
        var booking = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = later }, HttpStatusCode.Created));
        Assert.False((bool)booking["held"]!);
        var bookingId = booking.Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{bookingId}/transitions", new { state = "Resolved", resolutionNotes = "The hall is booked." });
        var due = (await requester.ArrayAsync($"/api/v1/service-requests/{bookingId}/payments")).Single()!;
        Assert.Equal(("AwaitingPayment", "OnCompletion"), ((string?)due["state"], (string?)due["timing"]));
        Assert.Null(due["expiresAt"]);
        using (var closing = await admin.SendAsync(HttpMethod.Post, $"/api/v1/service-requests/{bookingId}/transitions", new { state = "Closed" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, closing.StatusCode);
        }

        var pay = await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/{(string)due["id"]!}/pay", new { language = "en" });
        _gateway.ByReference((string)pay["reference"]!).Status = "CAPTURED";
        // The payer never came back and no callback came: the job asks the gateway.
        await _factory.Services.GetRequiredService<PaymentJobs>().RunTenantAsync(s.Tenant.Id, s.Tenant.Slug, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        Assert.Equal("Paid", (string?)(await requester.ArrayAsync($"/api/v1/service-requests/{bookingId}/payments")).Single()!["state"]);
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{bookingId}/transitions", new { state = "Closed" });

        // With payments off, fees are for information only.
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/payments", new { enabled = false });
        var free = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = upfront }, HttpStatusCode.Created);
        Assert.False((bool)free["held"]!);
        Assert.Empty(await requester.ArrayAsync($"/api/v1/service-requests/{free.Id()}/payments"));
    }

    [Fact]
    public async Task A_fee_due_after_approval_is_asked_for_once_the_request_is_approved()
    {
        var s = await SetUpAsync("pa");
        using var admin = s.Admin;
        using var publisher = s.Publisher;
        var approverUser = await _database.CreateUserAsync(s.Tenant, BuiltInRoles.Approver);
        using var approver = _factory.ForTenant(s.Tenant).As(approverUser);
        using var requester = await _factory.ForTenant(s.Tenant).SignedInAsync(_database, s.Tenant, BuiltInRoles.Requester);
        var service = await ServiceAsync(admin, publisher, "EVT-PERMIT", new JsonObject
        {
            ["name"] = Text("Event permit"), ["channels"] = new JsonArray("Portal"),
            ["fee"] = new JsonObject { ["paid"] = true, ["amountAed"] = 75, ["timing"] = "AfterApproval" },
            ["approvals"] = new JsonObject
            {
                ["beforeAssignment"] = new JsonArray(new JsonObject
                {
                    ["name"] = Text("Check"), ["mode"] = "Any",
                    ["approvers"] = new JsonArray(new JsonObject { ["kind"] = "User", ["id"] = approverUser.Id.ToString() }),
                }),
            },
        });
        var id = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = service }, HttpStatusCode.Created)).Id();
        Assert.Empty(await requester.ArrayAsync($"/api/v1/service-requests/{id}/payments"));
        var pending = await approver.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending");
        await approver.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{(string)pending["items"]![0]!["id"]!}/decision", new { decision = "Approve" });
        var waiting = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.Equal((true, "Payment"), ((bool)waiting["held"]!, (string?)waiting["hold"]));
        var payment = (await requester.ArrayAsync($"/api/v1/service-requests/{id}/payments")).Single()!;
        var pay = await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/{(string)payment["id"]!}/pay", new { language = "en" });
        _gateway.ByReference((string)pay["reference"]!).Status = "CAPTURED";
        await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/payments/attempts/{(string)pay["reference"]!}/confirm");
        var released = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.False((bool)released["held"]!);
        Assert.False((bool)released["awaitingApproval"]!);
    }

    [Theory]
    [InlineData("https://gw.test/p/{{reference}}?a={{amount}}", "https://gw.test/p/ZP%2F1?a=12.50")]
    [InlineData("{{ secret.apiKey }}-{{missing}}", "k-")]
    public void Templates_fill_placeholders_and_encode_addresses(string template, string expected)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["reference"] = "ZP/1", ["amount"] = "12.50", ["secret.apiKey"] = "k" };
        Assert.Equal(expected, Templates.Render(template, values, url: template.StartsWith("https", StringComparison.Ordinal)));
    }

    [Fact]
    public void Answers_are_read_by_path_and_signatures_checked_in_constant_time()
    {
        var node = JsonNode.Parse("""{"data":{"items":[{"id":"T1","n":5}]}}""");
        Assert.Equal(("T1", "5", null), (Templates.Read(node, "data.items.0.id"), Templates.Read(node, "data.items.0.n"), Templates.Read(node, "data.nope")));
        var json = new JsonObject();
        Templates.Write(json, "order.amount", "{{amountMinor}}", new Dictionary<string, string>(StringComparer.Ordinal) { ["amountMinor"] = "1250" });
        Assert.Equal("""{"order":{"amount":1250}}""", json.ToJsonString());
        var key = Guid.NewGuid().ToString("N");
        var payload = Encoding.UTF8.GetBytes("body");
        var settings = new SignatureSettings { Algorithm = SignatureAlgorithm.HmacSha256, Prefix = "sha256=" };
        Assert.True(Templates.Verify(settings, key, payload, "sha256=" + Templates.Sign(SignatureAlgorithm.HmacSha256, key, payload, SignatureEncoding.Hex)));
        Assert.False(Templates.Verify(settings, key, payload, "sha256=00"));
        Assert.NotEmpty(PaymentService.NewReference());
        Assert.NotEqual(PaymentService.NewReference(), PaymentService.NewReference());
    }
}

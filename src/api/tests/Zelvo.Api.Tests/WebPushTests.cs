using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Api.Notifications;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Notifications;
using Zelvo.Data.Tenancy;
using Zelvo.Notifications;
using Zelvo.Notifications.WebPush;

namespace Zelvo.Api.Tests;

/// <summary>A browser's side of web push for tests: its key pair and auth secret, made at run time, and RFC 8291 decryption.</summary>
internal sealed class TestBrowser : IDisposable
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public TestBrowser()
    {
        var q = _key.ExportParameters(false).Q;
        PublicKey = [4, .. q.X!, .. q.Y!];
        Auth = RandomNumberGenerator.GetBytes(16);
        Endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
    }

    public byte[] PublicKey { get; }

    public byte[] Auth { get; }

    public string Endpoint { get; }

    public object Subscription(string? deviceName = null) => new
    {
        endpoint = Endpoint,
        p256dh = Base64Url.EncodeToString(PublicKey),
        auth = Base64Url.EncodeToString(Auth),
        deviceName,
    };

    /// <summary>Decrypts an <c>aes128gcm</c> body as a browser does (RFC 8291 section 3.4 with .NET's HKDF).</summary>
    public byte[] Decrypt(byte[] body)
    {
        var salt = body[..16];
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
        var idLength = body[20];
        var serverPublic = body[21..(21 + idLength)];
        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..] },
        });
        var shared = _key.DeriveRawSecretAgreement(server.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Auth, [.. "WebPush: info\0"u8, .. PublicKey, .. serverPublic]);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());
        var cipher = body[(21 + idLength)..];
        var plain = new byte[cipher.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
        Assert.Equal(2, plain[^1]); // The last record's delimiter.
        return plain[..^1];
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Captures push messages instead of sending them; answers with <see cref="Answer"/>.</summary>
internal sealed class CapturingPushSender : IWebPushSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ConcurrentQueue<(string Endpoint, PushPayload Payload, string Urgency)> Sent { get; } = new();

    public Func<PushSubscription, PushResult> Answer { get; set; } = _ => new PushResult(PushResultKind.Sent);

    public Task<PushResult> SendAsync(PushSubscription subscription, byte[] payload, string publicKey, string privateKey, string urgency, CancellationToken cancellationToken)
    {
        var result = Answer(subscription);
        if (result.Kind == PushResultKind.Sent)
        {
            Sent.Enqueue((subscription.Endpoint, JsonSerializer.Deserialize<PushPayload>(payload, Json)!, urgency));
        }

        return Task.FromResult(result);
    }
}

/// <summary>Records requests to a push service and answers with a status.</summary>
internal sealed class PushServiceHandler(HttpStatusCode status) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request, await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
        return new HttpResponseMessage(status);
    }
}

/// <summary>Web push protocols (module 19, task J2; RFC 8030, 8291, 8292) without a database.</summary>
public sealed class WebPushProtocolTests
{
    [Fact]
    public void Messages_are_encrypted_for_the_browser_only_and_decrypt_with_RFC_8291()
    {
        using var browser = new TestBrowser();
        var payload = "{\"title\":\"بلاغ مسند إليكم\",\"body\":\"INC-000123\"}"u8.ToArray();
        var body = WebPushCrypto.Encrypt(payload, Base64Url.EncodeToString(browser.PublicKey), Base64Url.EncodeToString(browser.Auth));
        Assert.Equal(payload, browser.Decrypt(body));
        Assert.Equal(16 + 4 + 1 + 65 + payload.Length + 1 + 16, body.Length);

        // A fresh key and salt every time: the same message never looks the same twice.
        var again = WebPushCrypto.Encrypt(payload, Base64Url.EncodeToString(browser.PublicKey), Base64Url.EncodeToString(browser.Auth));
        Assert.NotEqual(body[..86], again[..86]);

        // Another browser cannot read it.
        using var other = new TestBrowser();
        Assert.ThrowsAny<CryptographicException>(() => other.Decrypt(body));
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPayloadLength + 1], Base64Url.EncodeToString(browser.PublicKey), Base64Url.EncodeToString(browser.Auth)));
    }

    [Fact]
    public void Subscription_keys_are_checked()
    {
        using var browser = new TestBrowser();
        var p256dh = Base64Url.EncodeToString(browser.PublicKey);
        Assert.True(WebPushCrypto.AreValidSubscriptionKeys(p256dh, Base64Url.EncodeToString(browser.Auth)));
        Assert.False(WebPushCrypto.AreValidSubscriptionKeys(p256dh, Base64Url.EncodeToString(new byte[8])));
        Assert.False(WebPushCrypto.AreValidSubscriptionKeys(Base64Url.EncodeToString(new byte[65]), Base64Url.EncodeToString(browser.Auth)));
        Assert.False(WebPushCrypto.AreValidSubscriptionKeys("not base64!", Base64Url.EncodeToString(browser.Auth)));
        Assert.False(WebPushCrypto.AreValidSubscriptionKeys(null, null));
    }

    [Fact]
    public void The_VAPID_token_names_the_push_service_and_verifies_with_the_public_key()
    {
        var (publicKey, privateKey) = WebPushCrypto.CreateKeyPair();
        var now = DateTimeOffset.UtcNow;
        var header = WebPushCrypto.VapidAuthorization(new Uri("https://fcm.googleapis.com/fcm/send/abc"), "https://help.example", publicKey, privateKey, now);
        Assert.StartsWith("vapid t=", header, StringComparison.Ordinal);
        Assert.EndsWith($", k={publicKey}", header, StringComparison.Ordinal);
        var token = header["vapid t=".Length..header.IndexOf(',', StringComparison.Ordinal)];
        var parts = token.Split('.');
        Assert.Equal("ES256", (string?)JsonNode.Parse(Base64Url.DecodeFromChars(parts[0]))!["alg"]);
        var claims = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1]))!;
        Assert.Equal("https://fcm.googleapis.com", (string?)claims["aud"]);
        Assert.Equal("https://help.example", (string?)claims["sub"]);
        Assert.InRange((long)claims["exp"]!, now.AddHours(11).ToUnixTimeSeconds(), now.AddHours(24).ToUnixTimeSeconds());

        var point = Base64Url.DecodeFromChars(publicKey);
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..] } });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/x", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/x", true)]
    [InlineData("https://web.push.apple.com/x", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=x", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/x", false)]
    [InlineData("https://fcm.googleapis.com.evil.example/x", false)]
    [InlineData("https://evilfcm.googleapis.com.example/x", false)]
    [InlineData("https://user@fcm.googleapis.com/x", false)]
    [InlineData("https://intranet.local/x", false)]
    [InlineData("not a url", false)]
    public void Only_the_known_push_services_are_accepted(string endpoint, bool allowed) =>
        Assert.Equal(allowed, new PushOptions().IsAllowedEndpoint(endpoint));

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 Chrome/130.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1", "Safari on iPhone")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36 Edg/130.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0", "Firefox on Linux")]
    [InlineData("", "Browser")]
    public void Devices_get_a_name_from_the_browser(string userAgent, string name) =>
        Assert.Equal(name, PushEndpoints.DeviceName(null, userAgent));

    [Fact]
    public async Task The_sender_posts_an_encrypted_message_with_VAPID_and_reads_the_push_services_answers()
    {
        using var browser = new TestBrowser();
        var (publicKey, privateKey) = WebPushCrypto.CreateKeyPair();
        var options = new NotificationOptions { WebUrl = new Uri("https://help.example/") };
        var subscription = new PushSubscription { Endpoint = browser.Endpoint, P256dh = Base64Url.EncodeToString(browser.PublicKey), Auth = Base64Url.EncodeToString(browser.Auth), DeviceName = "Test" };
        var payload = "{\"title\":\"x\"}"u8.ToArray();

        var handler = new PushServiceHandler(HttpStatusCode.Created);
        using (var sender = new WebPushSender(options, TimeProvider.System, handler))
        {
            Assert.Equal(PushResultKind.Sent, (await sender.SendAsync(subscription, payload, publicKey, privateKey, "high", TestContext.Current.CancellationToken)).Kind);
            Assert.Equal("https://help.example", sender.Subject);
        }

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(browser.Endpoint, request.RequestUri!.AbsoluteUri);
        Assert.Equal("aes128gcm", Assert.Single(request.Content!.Headers.ContentEncoding));
        Assert.Equal("86400", Assert.Single(request.Headers.GetValues("TTL")));
        Assert.Equal("high", Assert.Single(request.Headers.GetValues("Urgency")));
        Assert.StartsWith("vapid t=", Assert.Single(request.Headers.GetValues("Authorization")), StringComparison.Ordinal);
        Assert.Equal(payload, browser.Decrypt(body));

        foreach (var (status, kind) in new[]
        {
            (HttpStatusCode.Gone, PushResultKind.Gone), (HttpStatusCode.NotFound, PushResultKind.Gone), (HttpStatusCode.TooManyRequests, PushResultKind.Retry),
            (HttpStatusCode.BadGateway, PushResultKind.Retry), (HttpStatusCode.Forbidden, PushResultKind.Refused), (HttpStatusCode.RequestEntityTooLarge, PushResultKind.Refused),
        })
        {
            using var sender = new WebPushSender(options, TimeProvider.System, new PushServiceHandler(status));
            Assert.Equal(kind, (await sender.SendAsync(subscription, payload, publicKey, privateKey, "normal", TestContext.Current.CancellationToken)).Kind);
        }

        // A subscription that is not a known push service is never contacted.
        var stray = new PushServiceHandler(HttpStatusCode.Created);
        using (var sender = new WebPushSender(options, TimeProvider.System, stray))
        {
            var elsewhere = new PushSubscription { Endpoint = "https://intranet.example/hook", P256dh = subscription.P256dh, Auth = subscription.Auth, DeviceName = "x" };
            Assert.Equal(PushResultKind.Gone, (await sender.SendAsync(elsewhere, payload, publicKey, privateKey, "normal", TestContext.Current.CancellationToken)).Kind);
        }

        Assert.Empty(stray.Requests);
    }

    [Fact]
    public void Push_texts_name_the_event_and_reference_only_in_both_languages()
    {
        var values = new Dictionary<string, PlaceholderValue>
        {
            [Placeholders.TicketNumber] = PlaceholderValue.OfText("INC-000123"),
            [Placeholders.TicketTitle] = PlaceholderValue.OfText("Salary of Fatima"),
            [Placeholders.RequesterName] = PlaceholderValue.OfText("Fatima"),
        };
        Assert.Equal(("Incident assigned to you", "INC-000123"), PushNotifications.Text(NotificationEvents.IncidentAssigned, values, "en"));
        Assert.Equal(("بلاغ مسند إليكم", "INC-000123"), PushNotifications.Text(NotificationEvents.IncidentAssigned, values, "ar"));

        // Every event has a push title in both languages, and the Arabic follows the guidelines.
        foreach (var language in new[] { "en", "ar" })
        {
            var terms = NotificationTexts.For(language).Terms;
            foreach (var definition in NotificationEvents.All)
            {
                Assert.True(terms.TryGetValue($"push.title.{definition.Type}", out var title) && title.Length > 0, $"{language}: push.title.{definition.Type}");
                Assert.DoesNotContain("{{", title, StringComparison.Ordinal);
                Assert.DoesNotContain("!", title, StringComparison.Ordinal);
            }
        }
    }
}

/// <summary>Web push devices, preferences and delivery (task J2) against PostgreSQL.</summary>
public sealed class WebPushApiTests : IAsyncDisposable
{
    private readonly DatabaseFixture _database;
    private readonly CapturingPushSender _push = new();
    private readonly ApiFactory _factory;

    public WebPushApiTests(DatabaseFixture database)
    {
        _database = database;
        _factory = ApiFactory.ForDatabase(database).WithMasterKey().WithEmailSender(new CapturingEmailSender()).WithServices(s =>
        {
            s.RemoveAll<IWebPushSender>();
            s.AddSingleton<IWebPushSender>(_push);
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task An_agents_phone_gets_assignments_with_the_number_only_and_choices_are_kept()
    {
        var tenant = await _database.CreateTenantAsync("push");
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TeamLead);
        using var phone = new TestBrowser();

        // The tenant's key pair is made once and the public key handed to the browser.
        var status = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/me/push");
        Assert.True((bool)status["available"]!);
        var publicKey = (string)status["publicKey"]!;
        Assert.Equal(65, Base64Url.DecodeFromChars(publicKey).Length);
        Assert.Equal(publicKey, (string)(await agent.SendOkAsync(HttpMethod.Get, "/api/v1/me/push"))["publicKey"]!);

        // Addresses other than the push services and broken keys are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.SendAsync(HttpMethod.Post, "/api/v1/me/push/subscriptions",
            new { endpoint = "https://intranet.example/push", p256dh = Base64Url.EncodeToString(phone.PublicKey), auth = Base64Url.EncodeToString(phone.Auth) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.SendAsync(HttpMethod.Post, "/api/v1/me/push/subscriptions",
            new { endpoint = phone.Endpoint, p256dh = Base64Url.EncodeToString(new byte[65]), auth = Base64Url.EncodeToString(phone.Auth) })).StatusCode);

        var device = await agent.SendOkAsync(HttpMethod.Post, "/api/v1/me/push/subscriptions", phone.Subscription("Agent's phone"));
        Assert.Equal("Agent's phone", (string?)device["deviceName"]);
        // Subscribing again is the same device.
        Assert.Equal((string?)device["id"], (string?)(await agent.SendOkAsync(HttpMethod.Post, "/api/v1/me/push/subscriptions", phone.Subscription("Agent's phone")))["id"]);

        // An incident assigned to the agent: one push with the event and the number, never the title.
        var id = IncidentApi.IdOf(await lead.CreateAsync(new { title = "Salary slip of Fatima Al Mansoori", requesterId = agentUser.Id, channel = "Phone", assigneeId = agentUser.Id }));
        var number = (string)(await lead.GetIncidentAsync(id))["number"]!;
        await _factory.DeliverNotificationsAsync(tenant);
        await _factory.DeliverNotificationsAsync(tenant);
        var assigned = Assert.Single(_push.Sent, p => p.Payload.Title == "Incident assigned to you");
        Assert.Equal((phone.Endpoint, number, $"/en/incidents/{id}", id.ToString(), "high"), (assigned.Endpoint, assigned.Payload.Body, assigned.Payload.Url, assigned.Payload.Tag, assigned.Urgency));
        Assert.DoesNotContain(_push.Sent, p => p.Payload.Title.Contains("Fatima", StringComparison.Ordinal) || p.Payload.Body.Contains("Fatima", StringComparison.Ordinal));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id, tenant.Slug);
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            Assert.All(await db.PushDeliveries.AsNoTracking().ToListAsync(Ct), d => Assert.Equal(PushDeliveryStatus.Sent, d.Status));
            // The private key is stored encrypted.
            Assert.StartsWith("zv1.", (await db.WebPushKeys.AsNoTracking().SingleAsync(Ct)).PrivateKey, StringComparison.Ordinal);
        }

        // The agent turns push off for assignments: email stays, push stops.
        var preferences = await agent.SendOkAsync(HttpMethod.Put, "/api/v1/me/notification-preferences",
            new { events = new[] { new { eventType = NotificationEvents.IncidentAssigned, email = true, push = false } } });
        var choice = preferences["items"]!.AsArray().Single(p => (string)p!["eventType"]! == NotificationEvents.IncidentAssigned)!;
        Assert.Equal((true, false, true), ((bool)choice["email"]!, (bool)choice["push"]!, (bool)choice["pushEnabled"]!));
        _push.Sent.Clear();
        await lead.CreateAsync(new { title = "Second", requesterId = agentUser.Id, channel = "Phone", assigneeId = agentUser.Id });
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.DoesNotContain(_push.Sent, p => p.Payload.Title == "Incident assigned to you");

        // A test notification in the agent's language, audited.
        Assert.Equal(1, (int)(await agent.SendOkAsync(HttpMethod.Post, "/api/v1/me/push/test", new { }, HttpStatusCode.Accepted))["devices"]!);
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Contains(_push.Sent, p => p.Payload.Title == "Test notification" && p.Payload.Lang == "en" && p.Payload.Dir == "ltr");

        // The push service forgets the device: it is removed.
        _push.Answer = _ => new PushResult(PushResultKind.Gone, "http-410");
        await agent.SendOkAsync(HttpMethod.Post, "/api/v1/me/push/test", new { }, HttpStatusCode.Accepted);
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Empty((await agent.SendOkAsync(HttpMethod.Get, "/api/v1/me/push"))["devices"]!.AsArray());
        Assert.Equal(HttpStatusCode.Conflict, (await agent.SendAsync(HttpMethod.Post, "/api/v1/me/push/test", new { })).StatusCode);
    }

    [Fact]
    public async Task Devices_belong_to_one_person_and_go_at_sign_out_and_when_the_organisation_turns_push_off_for_an_event()
    {
        var tenant = await _database.CreateTenantAsync("push2");
        var firstUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var secondUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var first = _factory.ForTenant(tenant).As(firstUser);
        using var second = _factory.ForTenant(tenant).As(secondUser);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        using var browser = new TestBrowser();

        var device = (await first.SendOkAsync(HttpMethod.Post, "/api/v1/me/push/subscriptions", browser.Subscription())).Id();
        Assert.Equal(HttpStatusCode.NotFound, (await second.SendAsync(HttpMethod.Delete, $"/api/v1/me/push/subscriptions/{device}")).StatusCode);

        // Someone else signs in on the same browser: the device moves to them.
        await second.SendOkAsync(HttpMethod.Post, "/api/v1/me/push/subscriptions", browser.Subscription());
        Assert.Empty((await first.SendOkAsync(HttpMethod.Get, "/api/v1/me/push"))["devices"]!.AsArray());
        Assert.Single((await second.SendOkAsync(HttpMethod.Get, "/api/v1/me/push"))["devices"]!.AsArray());

        // Administrators switch push per event; the switch shows in Settings and in people's choices.
        var settings = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/notifications/settings");
        Assert.True((bool)settings["pushAvailable"]!);
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/notifications/settings/events/{NotificationEvents.IncidentReceived}", new { enabled = true, email = true, push = false });
        var received = (await second.SendOkAsync(HttpMethod.Get, "/api/v1/me/notification-preferences"))["items"]!.AsArray()
            .Single(p => (string)p!["eventType"]! == NotificationEvents.IncidentReceived)!;
        Assert.False((bool)received["pushEnabled"]!);
        await second.CreateAsync(new { title = "Printer" });
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Empty(_push.Sent);
        // Other switches leave push as it was.
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/notifications/settings/events/{NotificationEvents.IncidentReceived}", new { enabled = true, email = false });
        Assert.False((bool)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/notifications/settings"))["events"]!.AsArray()
            .Single(e => (string)e!["eventType"]! == NotificationEvents.IncidentReceived)!["push"]!);

        // Signing out on the device removes it.
        await second.SendOkAsync(HttpMethod.Post, "/api/v1/me/push/subscriptions/remove", new { endpoint = browser.Endpoint }, HttpStatusCode.NoContent);
        Assert.Empty((await second.SendOkAsync(HttpMethod.Get, "/api/v1/me/push"))["devices"]!.AsArray());
    }

    [Fact]
    public async Task Installations_without_internet_offer_no_push()
    {
        await using var offline = ApiFactory.ForDatabase(_database).WithSetting("Notifications:Push:Enabled", "false");
        var tenant = await _database.CreateTenantAsync("push3");
        using var person = await offline.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Requester);
        using var browser = new TestBrowser();
        var status = await person.SendOkAsync(HttpMethod.Get, "/api/v1/me/push");
        Assert.False((bool)status["available"]!);
        Assert.Null(status["publicKey"]);
        Assert.Equal(HttpStatusCode.Conflict, (await person.SendAsync(HttpMethod.Post, "/api/v1/me/push/subscriptions", browser.Subscription())).StatusCode);
        Assert.All((await person.SendOkAsync(HttpMethod.Get, "/api/v1/me/notification-preferences"))["items"]!.AsArray(), p => Assert.False((bool)p!["pushEnabled"]!));
    }
}

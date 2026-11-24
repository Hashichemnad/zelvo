using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailKit.Security;
using Zelvo.Channels;
using Zelvo.Data.Channels;
using Zelvo.Integrations;

namespace Zelvo.Api.Tests;

/// <summary>Microsoft Graph, the Gmail API and OAuth tokens (task E2), against a recording HTTP handler.</summary>
public sealed class MailProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static (MailboxClients Clients, OAuthTokens Tokens) Clients(Recorder recorder)
    {
        var options = new ChannelOptions();
        var http = new ChannelHttp(options, new OutboundAddressPolicy(options.Network), recorder);
        var tokens = new OAuthTokens(http, TimeProvider.System);
        return (new MailboxClients(http, tokens), tokens);
    }

    private static string RawEmail(string subject)
    {
        var message = ChannelApi.Email("a@b.example", "support@entity.example.gov", subject, "Hello");
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return Encoding.ASCII.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Microsoft_365_reads_unread_emails_marks_them_read_and_sends_as_the_mailbox_with_an_app_token()
    {
        var token = $"t-{Guid.NewGuid():N}";
        var recorder = new Recorder((request, _) => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal) => Json(new { access_token = token, expires_in = 3600 }),
            var p when p.EndsWith("/mailFolders/inbox/messages", StringComparison.Ordinal) => Json(new { value = new[] { new { id = "AAA" } } }),
            var p when p.EndsWith("/$value", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RawEmail("From Graph")) },
            _ => new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.OK),
        });
        var (clients, _) = Clients(recorder);
        var mailbox = new Mailbox
        {
            Name = "Support", Address = "support@entity.example.gov", Provider = MailboxProvider.Microsoft365, Auth = MailboxAuth.ClientSecret,
            ClientId = "app-id", DirectoryId = "contoso.example", TenantId = Guid.NewGuid(),
        };
        var credential = new MailboxCredential($"s-{Guid.NewGuid():N}");

        await using (var connection = await clients.OpenAsync(mailbox, credential, Ct))
        {
            Assert.Equal(["AAA"], await connection.ListUnreadAsync(10, Ct));
            Assert.Equal("From Graph", (await connection.GetAsync("AAA", 1_000_000, Ct))!.Subject);
            Assert.Null(await connection.GetAsync("AAA", 10, Ct)); // Too large.
            await connection.MarkReadAsync("AAA", Ct);
        }

        await clients.SendAsync(mailbox, credential, ChannelApi.Email("support@entity.example.gov", "x@y.example", "Re", "Hi"), Ct);

        var tokenRequest = recorder.Requests[0];
        Assert.Equal("https://login.microsoftonline.com/contoso.example/oauth2/v2.0/token", tokenRequest.Url);
        Assert.Contains("grant_type=client_credentials", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains(recorder.Requests, r => r.Url.StartsWith("https://graph.microsoft.com/v1.0/users/support%40entity.example.gov/mailFolders/inbox/messages?", StringComparison.Ordinal)
            && Uri.UnescapeDataString(r.Url).Contains("filter=isRead eq false", StringComparison.Ordinal));
        var mark = Assert.Single(recorder.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal("{\"isRead\":true}", mark.Body);
        var send = Assert.Single(recorder.Requests, r => r.Url.EndsWith("/sendMail", StringComparison.Ordinal));
        Assert.Equal($"Bearer {token}", send.Authorization);
        Assert.Contains("Subject: Re", Encoding.ASCII.GetString(Convert.FromBase64String(send.Body)), StringComparison.Ordinal);
        // One token for the whole read and the send.
        Assert.Single(recorder.Requests, r => r.Url.Contains("/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gmail_signs_in_as_the_mailbox_with_the_service_account_and_never_uses_the_key_files_token_address()
    {
        using var rsa = RSA.Create(2048);
        var key = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_email"] = "zelvo@project.iam.example",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = "https://attacker.example.net/token",
        });
        var recorder = new Recorder((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json(new { access_token = $"g-{Guid.NewGuid():N}", expires_in = 3600 }),
            "/gmail/v1/users/me/messages" => Json(new { messages = new[] { new { id = "m2" }, new { id = "m1" } } }),
            "/gmail/v1/users/me/messages/m1" => Json(new { raw = Base64Url.EncodeToString(Encoding.ASCII.GetBytes(RawEmail("From Gmail"))), sizeEstimate = 400 }),
            _ => Json(new { }),
        });
        var (clients, _) = Clients(recorder);
        var mailbox = new Mailbox { Name = "Support", Address = "support@entity.example.gov", Provider = MailboxProvider.Gmail, Auth = MailboxAuth.ServiceAccount, TenantId = Guid.NewGuid() };

        await using var connection = await clients.OpenAsync(mailbox, new MailboxCredential(key), Ct);
        Assert.Equal(["m1", "m2"], await connection.ListUnreadAsync(10, Ct)); // Oldest first.
        Assert.Equal("From Gmail", (await connection.GetAsync("m1", 1_000_000, Ct))!.Subject);
        await connection.MarkReadAsync("m1", Ct);

        var tokenRequest = recorder.Requests[0];
        Assert.Equal("https://oauth2.googleapis.com/token", tokenRequest.Url);
        var assertion = tokenRequest.Body.Split('&').Single(p => p.StartsWith("assertion=", StringComparison.Ordinal))["assertion=".Length..];
        var parts = Uri.UnescapeDataString(assertion).Split('.');
        var claims = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1]))!;
        Assert.Equal(("zelvo@project.iam.example", "support@entity.example.gov", "https://oauth2.googleapis.com/token", OAuthTokens.GmailScope),
            ((string?)claims["iss"], (string?)claims["sub"], (string?)claims["aud"], (string?)claims["scope"]));
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Contains(recorder.Requests, r => r.Url.EndsWith("/messages/m1/modify", StringComparison.Ordinal) && r.Body.Contains("UNREAD", StringComparison.Ordinal));
        Assert.DoesNotContain(recorder.Requests, r => r.Url.Contains("attacker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_administrators_sign_in_uses_PKCE_and_keeps_the_refresh_token()
    {
        var refresh = $"r-{Guid.NewGuid():N}";
        var idToken = $"{Base64Url.EncodeToString("{}"u8)}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"email\":\"Support@Entity.example.gov\"}"))}.x";
        var recorder = new Recorder((_, _) => Json(new { access_token = "a", refresh_token = refresh, id_token = idToken, expires_in = 3600 }));
        var (_, tokens) = Clients(recorder);
        var mailbox = new Mailbox { Name = "S", Address = "support@entity.example.gov", Provider = MailboxProvider.Microsoft365, Auth = MailboxAuth.OAuthSignIn, ClientId = "app-id" };
        var (verifier, challenge) = OAuthTokens.Pkce();
        var url = tokens.AuthorizeUrl(mailbox, new Uri("https://zelvo.entity.example.gov/auth/mailbox-callback"), "state-1", challenge);
        Assert.StartsWith("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", url.Query, StringComparison.Ordinal);
        Assert.Contains("offline_access", Uri.UnescapeDataString(url.Query), StringComparison.Ordinal);

        var (token, email) = await tokens.RedeemAsync(mailbox, $"c-{Guid.NewGuid():N}", "code-1", verifier, new Uri("https://zelvo.entity.example.gov/auth/mailbox-callback"), Ct);
        Assert.Equal((refresh, "support@entity.example.gov"), (token, email));
        Assert.Contains($"code_verifier={verifier}", recorder.Requests[0].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MailSecurity.Auto, 993, 993, SecureSocketOptions.SslOnConnect)]
    [InlineData(MailSecurity.Auto, 143, 993, SecureSocketOptions.StartTls)]
    [InlineData(MailSecurity.Auto, 587, 465, SecureSocketOptions.StartTls)]
    [InlineData(MailSecurity.None, 25, 465, SecureSocketOptions.None)]
    public void Credentials_never_travel_in_clear_unless_TLS_is_turned_off(MailSecurity security, int port, int implicitPort, SecureSocketOptions expected) =>
        Assert.Equal(expected, MailboxClients.Security(security, port, implicitPort));

    [Fact]
    public void Provider_errors_become_codes_administrators_can_act_on()
    {
        Assert.Equal("sign-in-expired", ChannelHttp.ProviderError(HttpStatusCode.BadRequest, "Renewing access", "{\"error\":\"invalid_grant\",\"error_description\":\"expired\"}").Code);
        Assert.Equal("access-denied", ChannelHttp.ProviderError(HttpStatusCode.Forbidden, "Reading", "{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"Access is denied.\"}}").Code);
        Assert.Contains("Access is denied.", ChannelHttp.ProviderError(HttpStatusCode.Forbidden, "Reading", "{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"Access is denied.\"}}").Message, StringComparison.Ordinal);
    }
}

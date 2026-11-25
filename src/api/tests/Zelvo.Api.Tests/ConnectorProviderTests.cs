using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Novell.Directory.Ldap;
using Zelvo.Connectors;
using Zelvo.Data.Connectors;
using Zelvo.Integrations;

namespace Zelvo.Api.Tests;

/// <summary>Microsoft Graph, the CRMs, REST calls and LDAP entries (task E7), against a recording HTTP handler.</summary>
public sealed class ConnectorProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Authorization, IReadOnlyDictionary<string, string> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase), body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static ConnectorClients Clients(Recorder recorder)
    {
        var options = new ConnectorOptions();
        return new ConnectorClients(options, new OutboundAddressPolicy(options.Network), recorder);
    }

    private static Connector Of(ConnectorKind kind, ConnectorSettings settings) => new() { Name = "test", Kind = kind, Settings = settings.Write() };

    [Fact]
    public async Task Entra_reads_users_with_managers_and_mapped_groups_across_pages_with_an_app_token()
    {
        var secret = MessagingApi.Secret("entra");
        var recorder = new Recorder((request, _) => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal) => Json(new { access_token = "graph-token", expires_in = 3600 }),
            var p when p.Contains("/groups/", StringComparison.Ordinal) => Json(new { value = new object[] { new { id = "u-1" } } }),
            _ when request.RequestUri!.Query.Contains("skiptoken", StringComparison.Ordinal) => Json(new
            {
                value = new[] { new { id = "u-2", displayName = "Omar", mail = (string?)null, userPrincipalName = "omar@entity.example.ae", department = "IT", accountEnabled = false, manager = new { id = "u-1" } } },
            }),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[{"id":"u-1","displayName":"Fatima","mail":"fatima@entity.example.ae","department":"IT","mobilePhone":"+971501234567","employeeId":"7","accountEnabled":true}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=X"}""",
                    Encoding.UTF8, "application/json"),
            },
        });
        var directory = Guid.NewGuid().ToString();
        var settings = new ConnectorSettings { Entra = new EntraSettings { DirectoryId = directory, ClientId = "app", GroupMappings = [new GroupMapping("g-1", Guid.NewGuid())] } };
        var people = await Clients(recorder).ReadDirectoryAsync(Of(ConnectorKind.EntraId, settings), settings, secret, new ExchangeLog(), Ct);

        Assert.Equal(["u-1", "u-2"], people.Select(p => p.ExternalId));
        Assert.Equal(("fatima@entity.example.ae", true), (people[0].Email, people[0].Enabled));
        Assert.Equal(["g-1"], people[0].Groups);
        Assert.Equal(("omar@entity.example.ae", false, "u-1"), (people[1].Email, people[1].Enabled, people[1].ManagerExternalId));
        Assert.Contains($"login.microsoftonline.com/{directory}/oauth2/v2.0/token", recorder.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", recorder.Requests[0].Body, StringComparison.Ordinal);
        Assert.All(recorder.Requests.Skip(1), r => Assert.Equal("Bearer graph-token", r.Authorization));
    }

    [Fact]
    public async Task Salesforce_signs_in_with_client_credentials_and_creates_then_updates_the_case_with_a_comment()
    {
        var recorder = new Recorder((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/services/oauth2/token" => Json(new { access_token = "sf-token", instance_url = "https://entity.my.salesforce.com" }),
            var p when p.EndsWith("/sobjects/Case", StringComparison.Ordinal) => Json(new { id = "500A", success = true }, HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });
        var settings = new ConnectorSettings { Crm = new CrmSettings { Url = "https://entity.my.salesforce.com", ClientId = "client" } };
        var connector = Of(ConnectorKind.Salesforce, settings);
        var clients = Clients(recorder);
        var id = await clients.SaveCaseAsync(connector, settings, MessagingApi.Secret("sf"), new CrmCase(null, "003A", "INC-000001", "Portal down", "Error", "New", false, null, "https://z/1"), new ExchangeLog(), Ct);
        await clients.SaveCaseAsync(connector, settings, MessagingApi.Secret("sf"), new CrmCase(id, "003A", "INC-000001", "Portal down", "Error", "Closed", false, "INC-000001: Resolved", "https://z/1"),
            new ExchangeLog(), Ct);

        Assert.Equal("500A", id);
        Assert.Contains("grant_type=client_credentials", recorder.Requests[0].Body, StringComparison.Ordinal);
        var created = JsonNode.Parse(recorder.Requests[1].Body)!;
        Assert.Equal(("003A", "New", "INC-000001: Portal down"), ((string?)created["ContactId"], (string?)created["Status"], (string?)created["Subject"]));
        Assert.Equal((HttpMethod.Patch, "Closed"), (recorder.Requests[2].Method, (string?)JsonNode.Parse(recorder.Requests[2].Body)!["Status"]));
        Assert.EndsWith("/sobjects/CaseComment", recorder.Requests[3].Url, StringComparison.Ordinal);
        // One token for both: cached until a minute before it expires.
        Assert.Single(recorder.Requests, r => r.Url.EndsWith("/services/oauth2/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HubSpot_and_Zoho_tickets_are_associated_with_the_contact_and_Zoho_refreshes_its_token()
    {
        var hubspot = new Recorder((_, _) => Json(new { id = "9001" }, HttpStatusCode.Created));
        var token = MessagingApi.Secret("hs");
        await Clients(hubspot).SaveCaseAsync(Of(ConnectorKind.HubSpot, new()), new(), token, new CrmCase(null, "51", "INC-000002", "VPN", null, "1", false, null, "https://z/2"), new ExchangeLog(), Ct);
        var ticket = JsonNode.Parse(hubspot.Requests[0].Body)!;
        Assert.Equal(($"Bearer {token}", "51", 16), (hubspot.Requests[0].Authorization, (string?)ticket["associations"]![0]!["to"]!["id"], (int)ticket["associations"]![0]!["types"]![0]!["associationTypeId"]!));

        var zoho = new Recorder((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/oauth/v2/token", StringComparison.Ordinal)
            ? Json(new { access_token = "zoho-token", expires_in = 3600 })
            : Json(new { data = new[] { new { details = new { id = "Z1" } } } }, HttpStatusCode.Created));
        var settings = new ConnectorSettings { Crm = new CrmSettings { Url = "https://accounts.zoho.com", ApiUrl = "https://www.zohoapis.com", ClientId = "1000.X" } };
        var refresh = MessagingApi.Secret("refresh");
        var id = await Clients(zoho).SaveCaseAsync(Of(ConnectorKind.Zoho, settings), settings, new ZohoCredential(MessagingApi.Secret("zs"), refresh).Write(),
            new CrmCase(null, "C77", "INC-000003", "Printer", null, "New", false, null, "https://z/3"), new ExchangeLog(), Ct);
        Assert.Equal("Z1", id);
        Assert.Contains($"refresh_token={refresh}", zoho.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("Zoho-oauthtoken zoho-token", zoho.Requests[1].Authorization);
        Assert.Equal("C77", (string?)JsonNode.Parse(zoho.Requests[1].Body)!["data"]![0]!["Related_To"]!["id"]);
    }

    [Fact]
    public async Task REST_calls_carry_their_authentication_and_refusals_are_classified()
    {
        var recorder = new Recorder((_, _) => Json(new { ok = true }));
        var key = MessagingApi.Secret("key");
        var settings = new RestSettings { BaseUrl = "https://api.example.ae", Auth = RestAuth.ApiKey, AuthHeader = "X-API-Key" };
        var answer = await Clients(recorder).CallAsync(Of(ConnectorKind.Rest, new()), settings, key,
            new RestCall("POST", new Uri("https://api.example.ae/cases"), new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Source"] = "zelvo" }, "{}"), new ExchangeLog(), Ct);
        Assert.Equal(200, answer.Status);
        Assert.Equal((key, "zelvo"), (recorder.Requests[0].Headers["X-API-Key"], recorder.Requests[0].Headers["X-Source"]));

        Assert.Equal(("refused", true), (ConnectorClients.Error(422, "h", "bad").Code, ConnectorClients.Error(422, "h", "bad").Permanent));
        Assert.Equal(("rate-limited", false), (ConnectorClients.Error(429, "h", "").Code, ConnectorClients.Error(429, "h", "").Permanent));
        Assert.Equal(("provider-error", false), (ConnectorClients.Error(503, "h", "").Code, ConnectorClients.Error(503, "h", "").Permanent));

        // Settings are checked: paths stay on the base address, placeholders are known, no secrets in headers.
        var bad = new RestSettings
        {
            BaseUrl = "https://api.example.ae",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "x" },
            Operations = [new RestOperation { Key = "a", Name = new("A", null), Path = "//evil.example/{{ticket.secret}}" }],
        };
        var fields = bad.Problems().Select(p => p.Field).ToList();
        Assert.Contains("rest.headers", fields);
        Assert.Contains("rest.operations.0.path", fields);
        Assert.Contains("rest.operations.0.placeholders", fields);
    }

    [Fact]
    public void LDAP_entries_become_people_with_their_manager_groups_and_Active_Directory_state()
    {
        var guid = Guid.NewGuid();
        var attributes = new LdapAttributeSet
        {
            new LdapAttribute("objectGUID", guid.ToByteArray()),
            new LdapAttribute("mail", "salem@entity.example.ae"),
            new LdapAttribute("displayName", "Salem"),
            new LdapAttribute("department", "Finance"),
            new LdapAttribute("manager", "CN=Boss,OU=Staff,DC=entity,DC=ae"),
            new LdapAttribute("memberOf", ["CN=Finance,OU=Groups,DC=entity,DC=ae", "CN=Other,OU=Groups,DC=entity,DC=ae"]),
            new LdapAttribute("userAccountControl", "514"),
        };
        var person = ConnectorClients.LdapPerson(new LdapEntry("CN=Salem,OU=Staff,DC=entity,DC=ae", attributes), new LdapAttributes(), activeDirectory: true,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CN=Boss,OU=Staff,DC=entity,DC=ae"] = "boss-id" },
            new HashSet<string>(["CN=Finance,OU=Groups,DC=entity,DC=ae"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal((guid.ToString(), "salem@entity.example.ae", "Finance", "boss-id", false), (person.ExternalId, person.Email, person.Department, person.ManagerExternalId, person.Enabled));
        Assert.Equal(["CN=Finance,OU=Groups,DC=entity,DC=ae"], person.Groups);
    }
}

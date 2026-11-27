using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Novell.Directory.Ldap;
using Zelvo.Data.Connectors;
using Zelvo.Integrations;

namespace Zelvo.Connectors;

/// <summary>A connected system refused a call or could not be reached.</summary>
/// <param name="code">Stable code (<c>sign-in-failed</c>, <c>unreachable</c>, <c>refused</c>, <c>address-not-allowed</c>…).</param>
/// <param name="message">What happened.</param>
/// <param name="permanent">Trying again will not help.</param>
public sealed class ConnectorException(string code, string message, bool permanent = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Stable code.</summary>
    public string Code { get; } = code;

    /// <summary>Whether trying again will not help.</summary>
    public bool Permanent { get; } = permanent;

    /// <summary>Code and message.</summary>
    public string Summary => $"{Code}: {Message}";
}

/// <summary>One HTTP exchange with a connected system, for the integration log (bodies masked when written).</summary>
public sealed record HttpExchange(string Method, string Url, int? Status, int DurationMs, string? RequestBody, string? RequestType, string? ResponseBody, string? ResponseType, string? Error);

/// <summary>The exchanges of one connector operation.</summary>
public sealed class ExchangeLog
{
    /// <summary>What was sent and answered, in order.</summary>
    public List<HttpExchange> Exchanges { get; } = [];
}

/// <summary>A person of a directory.</summary>
public sealed record DirectoryPerson(
    string ExternalId, string? Email, string? Name, string? Department, string? ManagerExternalId, string? Phone, string? EmployeeId, bool Enabled,
    IReadOnlyCollection<string> Groups);

/// <summary>A contact of a CRM.</summary>
public sealed record CrmContact(string ExternalId, string Email, string? Name, string? Phone, string? CompanyId, string? CompanyName);

/// <summary>A ticket as its CRM case: created when <paramref name="CaseId"/> is null, else updated; a note is added when given.</summary>
public sealed record CrmCase(string? CaseId, string ContactId, string Number, string Title, string? Description, string Status, bool Closing, string? Note, string Url);

/// <summary>A REST call to make (already rendered).</summary>
public sealed record RestCall(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>A REST call's answer.</summary>
public sealed record RestAnswer(int Status, string? ContentType, string Body, int DurationMs);

/// <summary>Talks to the connected systems (task E7). Replaced in tests.</summary>
public interface IConnectorClients
{
    /// <summary>Everyone in the directory (Entra ID or LDAP) with their groups.</summary>
    Task<IReadOnlyList<DirectoryPerson>> ReadDirectoryAsync(Connector connector, ConnectorSettings settings, string? credential, ExchangeLog log, CancellationToken cancellationToken);

    /// <summary>The CRM's contacts changed since <paramref name="since"/> (all when null).</summary>
    Task<IReadOnlyList<CrmContact>> ReadContactsAsync(Connector connector, ConnectorSettings settings, string? credential, DateTimeOffset? since, ExchangeLog log, CancellationToken cancellationToken);

    /// <summary>Creates or updates the ticket's case; returns its id.</summary>
    Task<string> SaveCaseAsync(Connector connector, ConnectorSettings settings, string? credential, CrmCase ticketCase, ExchangeLog log, CancellationToken cancellationToken);

    /// <summary>Sends a REST connector's call.</summary>
    Task<RestAnswer> CallAsync(Connector connector, RestSettings settings, string? credential, RestCall request, ExchangeLog log, CancellationToken cancellationToken);

    /// <summary>Checks the connection and credential; returns what the system says.</summary>
    Task<string> TestAsync(Connector connector, ConnectorSettings settings, string? credential, ExchangeLog log, CancellationToken cancellationToken);
}

/// <summary>
/// Zelvo's own thin clients for Microsoft Graph, LDAP (Novell.Directory.Ldap, MIT), Dynamics 365
/// (Dataverse Web API), Salesforce, HubSpot, Zoho CRM and REST APIs set up on screen. Every HTTP call
/// and LDAP connection follows the connectors' outgoing-request policy
/// (<c>Connectors:Network:AllowedPrivateNetworks</c>): internal addresses only when the operator allows them.
/// </summary>
public sealed partial class ConnectorClients : IConnectorClients, IDisposable
{
    private const int MaxBody = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly ConnectorOptions _options;
    private readonly HttpMessageHandler? _testHandler;
    private readonly ConcurrentDictionary<string, (string Token, string? Instance, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);

    /// <summary>Creates the clients.</summary>
    public ConnectorClients(ConnectorOptions options, OutboundAddressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        _options = options;
        Policy = policy;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, cancellationToken) => policy.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken),
        })
        { Timeout = options.Timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Zelvo/1.0");
    }

    /// <summary>Tests only: sends through <paramref name="handler"/>.</summary>
    internal ConnectorClients(ConnectorOptions options, OutboundAddressPolicy policy, HttpMessageHandler handler)
    {
        _testHandler = handler;
        _options = options;
        Policy = policy;
        _http = new HttpClient(handler) { Timeout = options.Timeout };
    }

    /// <summary>The policy connections follow.</summary>
    public OutboundAddressPolicy Policy { get; }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryPerson>> ReadDirectoryAsync(Connector connector, ConnectorSettings settings, string? credential, ExchangeLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(settings);
        return connector.Kind == ConnectorKind.EntraId
            ? EntraUsersAsync(connector, settings.Entra ?? throw NotSetUp(), credential, log, cancellationToken)
            : LdapPeopleAsync(settings.Ldap ?? throw NotSetUp(), credential, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrmContact>> ReadContactsAsync(
        Connector connector, ConnectorSettings settings, string? credential, DateTimeOffset? since, ExchangeLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(settings);
        var crm = settings.Crm ?? new CrmSettings();
        var contacts = new List<CrmContact>();
        switch (connector.Kind)
        {
            case ConnectorKind.Dynamics365:
            {
                var org = Base(crm.Url);
                var filter = since is { } s ? $"&$filter=modifiedon gt {s.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}" : string.Empty;
                Uri? next = new(org, $"api/data/v9.2/contacts?$select=contactid,fullname,emailaddress1,mobilephone,_parentcustomerid_value{filter}");
                while (next is not null && contacts.Count < _options.MaxRecords)
                {
                    var page = await JsonAsync(HttpMethod.Get, next, await DynamicsTokenAsync(connector, crm, credential, log, cancellationToken), null, log, cancellationToken,
                        [("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\",odata.maxpagesize=500")]);
                    foreach (var c in page["value"] as JsonArray ?? [])
                    {
                        if (Str(c, "emailaddress1") is { } email && Str(c, "contactid") is { } id)
                        {
                            contacts.Add(new CrmContact(id, email, Str(c, "fullname"), Str(c, "mobilephone"), Str(c, "_parentcustomerid_value"),
                                Str(c, "_parentcustomerid_value@OData.Community.Display.V1.FormattedValue")));
                        }
                    }

                    next = Str(page, "@odata.nextLink") is { } link ? new Uri(link) : null;
                }

                break;
            }

            case ConnectorKind.Salesforce:
            {
                var (token, instance) = await SalesforceTokenAsync(connector, crm, credential, log, cancellationToken);
                var where = since is { } s ? $" WHERE LastModifiedDate > {s.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}" : string.Empty;
                var soql = $"SELECT Id, Name, Email, MobilePhone, AccountId, Account.Name FROM Contact{where} ORDER BY LastModifiedDate";
                Uri? next = new(instance, $"services/data/{_options.SalesforceApiVersion}/query?q={Uri.EscapeDataString(soql)}");
                while (next is not null && contacts.Count < _options.MaxRecords)
                {
                    var page = await JsonAsync(HttpMethod.Get, next, token, null, log, cancellationToken);
                    foreach (var c in page["records"] as JsonArray ?? [])
                    {
                        if (Str(c, "Email") is { } email && Str(c, "Id") is { } id)
                        {
                            contacts.Add(new CrmContact(id, email, Str(c, "Name"), Str(c, "MobilePhone"), Str(c, "AccountId"), Str(c?["Account"], "Name")));
                        }
                    }

                    next = Str(page, "nextRecordsUrl") is { } more ? new Uri(instance, more.TrimStart('/')) : null;
                }

                break;
            }

            case ConnectorKind.HubSpot:
            {
                string? after = null;
                do
                {
                    var body = new JsonObject
                    {
                        ["properties"] = new JsonArray("email", "firstname", "lastname", "mobilephone", "company", "associatedcompanyid"),
                        ["limit"] = 100,
                        ["sorts"] = new JsonArray(new JsonObject { ["propertyName"] = "lastmodifieddate", ["direction"] = "ASCENDING" }),
                    };
                    if (since is { } s)
                    {
                        body["filterGroups"] = new JsonArray(new JsonObject
                        {
                            ["filters"] = new JsonArray(new JsonObject
                            {
                                ["propertyName"] = "lastmodifieddate", ["operator"] = "GT", ["value"] = s.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                            }),
                        });
                    }

                    if (after is not null)
                    {
                        body["after"] = after;
                    }

                    var page = await JsonAsync(HttpMethod.Post, new Uri(_options.HubSpotUrl, "crm/v3/objects/contacts/search"), Require(credential, "access token"), body, log, cancellationToken);
                    foreach (var c in page["results"] as JsonArray ?? [])
                    {
                        var p = c?["properties"];
                        if (Str(p, "email") is { } email && Str(c, "id") is { } id)
                        {
                            var name = $"{Str(p, "firstname")} {Str(p, "lastname")}".Trim();
                            contacts.Add(new CrmContact(id, email, name.Length > 0 ? name : null, Str(p, "mobilephone"), Str(p, "associatedcompanyid"), Str(p, "company")));
                        }
                    }

                    after = Str(page?["paging"]?["next"], "after");
                }
                while (after is not null && contacts.Count < _options.MaxRecords);

                break;
            }

            case ConnectorKind.Zoho:
            {
                var token = await ZohoTokenAsync(connector, crm, credential, log, cancellationToken);
                var api = Base(crm.ApiUrl);
                for (var page = 1; contacts.Count < _options.MaxRecords; page++)
                {
                    var headers = new List<(string Name, string Value)>();
                    if (since is { } s)
                    {
                        headers.Add(("If-Modified-Since", s.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)));
                    }

                    var json = await JsonAsync(HttpMethod.Get, new Uri(api, $"crm/v6/Contacts?fields=Email,Full_Name,Mobile,Account_Name&per_page=200&page={page}"),
                        token, null, log, cancellationToken, [.. headers], scheme: "Zoho-oauthtoken");
                    foreach (var c in json["data"] as JsonArray ?? [])
                    {
                        if (Str(c, "Email") is { } email && Str(c, "id") is { } id)
                        {
                            contacts.Add(new CrmContact(id, email, Str(c, "Full_Name"), Str(c, "Mobile"), Str(c?["Account_Name"], "id"), Str(c?["Account_Name"], "name")));
                        }
                    }

                    if (json["info"]?["more_records"]?.GetValue<bool>() != true)
                    {
                        break;
                    }
                }

                break;
            }

            default:
                throw NotSetUp();
        }

        return contacts;
    }

    /// <inheritdoc />
    public async Task<string> SaveCaseAsync(Connector connector, ConnectorSettings settings, string? credential, CrmCase ticketCase, ExchangeLog log, CancellationToken cancellationToken)
    {
        var @case = ticketCase;
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ticketCase);
        var crm = settings.Crm ?? new CrmSettings();
        switch (connector.Kind)
        {
            case ConnectorKind.Dynamics365:
            {
                var org = Base(crm.Url);
                var token = await DynamicsTokenAsync(connector, crm, credential, log, cancellationToken);
                var id = @case.CaseId;
                if (id is null)
                {
                    var created = await JsonAsync(HttpMethod.Post, new Uri(org, "api/data/v9.2/incidents"), token, new JsonObject
                    {
                        ["title"] = Cut(@case.Title, 200),
                        ["description"] = Cut($"{@case.Number}\n{@case.Description}\n{@case.Url}", 2000),
                        ["customerid_contact@odata.bind"] = $"/contacts({@case.ContactId})",
                    }, log, cancellationToken, [("Prefer", "return=representation")]);
                    id = Str(created, "incidentid") ?? throw new ConnectorException("bad-answer", "Dynamics did not return the case's id.");
                }

                if (@case.Note is { } note)
                {
                    await JsonAsync(HttpMethod.Post, new Uri(org, "api/data/v9.2/annotations"), token, new JsonObject
                    {
                        ["subject"] = @case.Number, ["notetext"] = note, ["objectid_incident@odata.bind"] = $"/incidents({id})",
                    }, log, cancellationToken);
                }

                if (@case.Closing)
                {
                    // Dataverse closes a case through its action, with a resolution.
                    await JsonAsync(HttpMethod.Post, new Uri(org, "api/data/v9.2/CloseIncident"), token, new JsonObject
                    {
                        ["IncidentResolution"] = new JsonObject { ["subject"] = Cut(@case.Note ?? @case.Number, 200), ["incidentid@odata.bind"] = $"/incidents({id})" },
                        ["Status"] = int.TryParse(@case.Status, CultureInfo.InvariantCulture, out var closed) ? closed : 5,
                    }, log, cancellationToken);
                }
                else if (int.TryParse(@case.Status, CultureInfo.InvariantCulture, out var status))
                {
                    await JsonAsync(HttpMethod.Patch, new Uri(org, $"api/data/v9.2/incidents({id})"), token, new JsonObject { ["statuscode"] = status }, log, cancellationToken);
                }

                return id;
            }

            case ConnectorKind.Salesforce:
            {
                var (token, instance) = await SalesforceTokenAsync(connector, crm, credential, log, cancellationToken);
                var root = new Uri(instance, $"services/data/{_options.SalesforceApiVersion}/");
                var id = @case.CaseId;
                if (id is null)
                {
                    var created = await JsonAsync(HttpMethod.Post, new Uri(root, "sobjects/Case"), token, new JsonObject
                    {
                        ["Subject"] = Cut($"{@case.Number}: {@case.Title}", 255),
                        ["Description"] = Cut($"{@case.Description}\n{@case.Url}", 32000),
                        ["ContactId"] = @case.ContactId,
                        ["Status"] = @case.Status,
                        ["Origin"] = "Web",
                    }, log, cancellationToken);
                    id = Str(created, "id") ?? throw new ConnectorException("bad-answer", "Salesforce did not return the case's id.");
                }
                else
                {
                    await JsonAsync(HttpMethod.Patch, new Uri(root, $"sobjects/Case/{Uri.EscapeDataString(id)}"), token, new JsonObject { ["Status"] = @case.Status }, log, cancellationToken);
                }

                if (@case.Note is { } note)
                {
                    await JsonAsync(HttpMethod.Post, new Uri(root, "sobjects/CaseComment"), token, new JsonObject { ["ParentId"] = id, ["CommentBody"] = Cut(note, 4000) }, log, cancellationToken);
                }

                return id;
            }

            case ConnectorKind.HubSpot:
            {
                var token = Require(credential, "access token");
                var id = @case.CaseId;
                if (id is null)
                {
                    var created = await JsonAsync(HttpMethod.Post, new Uri(_options.HubSpotUrl, "crm/v3/objects/tickets"), token, new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            ["subject"] = Cut($"{@case.Number}: {@case.Title}", 255), ["content"] = $"{@case.Description}\n{@case.Url}", ["hs_pipeline"] = "0",
                            ["hs_pipeline_stage"] = @case.Status,
                        },
                        // HubSpot-defined association 16: ticket to contact.
                        ["associations"] = new JsonArray(Association(@case.ContactId, 16)),
                    }, log, cancellationToken);
                    id = Str(created, "id") ?? throw new ConnectorException("bad-answer", "HubSpot did not return the ticket's id.");
                }
                else
                {
                    await JsonAsync(HttpMethod.Patch, new Uri(_options.HubSpotUrl, $"crm/v3/objects/tickets/{Uri.EscapeDataString(id)}"), token,
                        new JsonObject { ["properties"] = new JsonObject { ["hs_pipeline_stage"] = @case.Status } }, log, cancellationToken);
                }

                if (@case.Note is { } note)
                {
                    // Association 228: note to ticket.
                    await JsonAsync(HttpMethod.Post, new Uri(_options.HubSpotUrl, "crm/v3/objects/notes"), token, new JsonObject
                    {
                        ["properties"] = new JsonObject { ["hs_note_body"] = note, ["hs_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) },
                        ["associations"] = new JsonArray(Association(id, 228)),
                    }, log, cancellationToken);
                }

                return id;
            }

            case ConnectorKind.Zoho:
            {
                var token = await ZohoTokenAsync(connector, crm, credential, log, cancellationToken);
                var api = Base(crm.ApiUrl);
                var id = @case.CaseId;
                if (id is null)
                {
                    var created = await JsonAsync(HttpMethod.Post, new Uri(api, "crm/v6/Cases"), token, new JsonObject
                    {
                        ["data"] = new JsonArray(new JsonObject
                        {
                            ["Subject"] = Cut($"{@case.Number}: {@case.Title}", 255), ["Description"] = $"{@case.Description}\n{@case.Url}", ["Status"] = @case.Status,
                            ["Case_Origin"] = "Web", ["Related_To"] = new JsonObject { ["id"] = @case.ContactId },
                        }),
                    }, log, cancellationToken, scheme: "Zoho-oauthtoken");
                    id = Str(created?["data"]?[0]?["details"], "id") ?? throw new ConnectorException("bad-answer", "Zoho did not return the case's id.");
                }
                else
                {
                    await JsonAsync(HttpMethod.Put, new Uri(api, $"crm/v6/Cases/{Uri.EscapeDataString(id)}"), token,
                        new JsonObject { ["data"] = new JsonArray(new JsonObject { ["Status"] = @case.Status }) }, log, cancellationToken, scheme: "Zoho-oauthtoken");
                }

                if (@case.Note is { } note)
                {
                    await JsonAsync(HttpMethod.Post, new Uri(api, "crm/v6/Notes"), token, new JsonObject
                    {
                        ["data"] = new JsonArray(new JsonObject
                        {
                            ["Note_Title"] = @case.Number, ["Note_Content"] = note,
                            ["Parent_Id"] = new JsonObject { ["module"] = new JsonObject { ["api_name"] = "Cases" }, ["id"] = id },
                        }),
                    }, log, cancellationToken, scheme: "Zoho-oauthtoken");
                }

                return id;
            }

            default:
                throw NotSetUp();
        }
    }

    /// <inheritdoc />
    public async Task<RestAnswer> CallAsync(Connector connector, RestSettings settings, string? credential, RestCall request, ExchangeLog log, CancellationToken cancellationToken)
    {
        var call = request;
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(log);
        using var message = new HttpRequestMessage(new HttpMethod(call.Method), call.Url);
        foreach (var (name, value) in call.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        switch (settings.Auth)
        {
            case RestAuth.ApiKey:
                message.Headers.TryAddWithoutValidation(settings.AuthHeader!, Require(credential, "key"));
                break;
            case RestAuth.Basic:
                message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{credential}")));
                break;
            case RestAuth.Bearer:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Require(credential, "token"));
                break;
            case RestAuth.OAuth2:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await RestTokenAsync(connector, settings, credential, log, cancellationToken));
                break;
        }

        if (call.Body is not null)
        {
            message.Content = new StringContent(call.Body, Encoding.UTF8, "application/json");
        }

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var (status, type, body, duration) = await SendAsync(message, call.Body, log, cancellationToken);
        return new RestAnswer(status, type, body, duration);
    }

    /// <inheritdoc />
    public async Task<string> TestAsync(Connector connector, ConnectorSettings settings, string? credential, ExchangeLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(settings);
        switch (connector.Kind)
        {
            case ConnectorKind.EntraId:
            {
                var entra = settings.Entra ?? throw NotSetUp();
                var token = await MicrosoftTokenAsync(connector.Id, entra.DirectoryId, entra.ClientId, credential, new Uri(_options.GraphUrl, ".default").AbsoluteUri, log, cancellationToken);
                var org = await JsonAsync(HttpMethod.Get, new Uri(_options.GraphUrl, "v1.0/organization?$select=displayName"), token, null, log, cancellationToken);
                return Str(org["value"]?[0], "displayName") ?? entra.DirectoryId;
            }

            case ConnectorKind.Ldap:
            {
                var ldap = settings.Ldap ?? throw NotSetUp();
                using var connection = await LdapConnectAsync(ldap, credential, cancellationToken);
                return $"{ldap.Host}: {ldap.BindDn}";
            }

            case ConnectorKind.Rest:
                return new Uri(settings.Rest?.BaseUrl ?? throw NotSetUp()).Host;

            default:
                var contacts = await ReadContactsAsync(connector, settings, credential, DateTimeOffset.UtcNow.AddMinutes(-1), log, cancellationToken);
                return contacts.Count.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Forgets cached tokens of a connector (its settings or credential changed).</summary>
    public void Forget(Guid connectorId)
    {
        foreach (var key in _tokens.Keys.Where(k => k.StartsWith(connectorId.ToString("N"), StringComparison.Ordinal)))
        {
            _tokens.TryRemove(key, out _);
        }
    }

    private async Task<IReadOnlyList<DirectoryPerson>> EntraUsersAsync(Connector connector, EntraSettings entra, string? credential, ExchangeLog log, CancellationToken cancellationToken)
    {
        var token = await MicrosoftTokenAsync(connector.Id, entra.DirectoryId, entra.ClientId, credential, new Uri(_options.GraphUrl, ".default").AbsoluteUri, log, cancellationToken);
        async Task<HashSet<string>> MembersAsync(string groupId)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            Uri? next = new(_options.GraphUrl, $"v1.0/groups/{Uri.EscapeDataString(groupId)}/transitiveMembers?$select=id&$top=999");
            while (next is not null)
            {
                var page = await JsonAsync(HttpMethod.Get, next, token, null, log, cancellationToken);
                foreach (var m in page["value"] as JsonArray ?? [])
                {
                    if (Str(m, "@odata.type") is null or "#microsoft.graph.user" && Str(m, "id") is { } id)
                    {
                        ids.Add(id);
                    }
                }

                next = Str(page, "@odata.nextLink") is { } link ? new Uri(link) : null;
            }

            return ids;
        }

        var scope = entra.UsersGroupId is { Length: > 0 } usersGroup ? await MembersAsync(usersGroup) : null;
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var mapping in entra.GroupMappings)
        {
            groups[mapping.ExternalId] = await MembersAsync(mapping.ExternalId);
        }

        var people = new List<DirectoryPerson>();
        Uri? usersUrl = new(_options.GraphUrl,
            "v1.0/users?$select=id,displayName,mail,userPrincipalName,department,mobilePhone,employeeId,accountEnabled&$expand=manager($select=id)&$top=999");
        while (usersUrl is not null && people.Count < _options.MaxRecords)
        {
            var page = await JsonAsync(HttpMethod.Get, usersUrl, token, null, log, cancellationToken);
            foreach (var u in page["value"] as JsonArray ?? [])
            {
                if (Str(u, "id") is not { } id || scope is not null && !scope.Contains(id))
                {
                    continue;
                }

                var enabled = u?["accountEnabled"] is JsonValue v && v.TryGetValue<bool>(out var on) ? on : true;
                people.Add(new DirectoryPerson(id, Str(u, "mail") ?? Str(u, "userPrincipalName"), Str(u, "displayName"), Str(u, "department"),
                    Str(u?["manager"], "id"), Str(u, "mobilePhone"), Str(u, "employeeId"), enabled,
                    [.. groups.Where(g => g.Value.Contains(id)).Select(g => g.Key)]));
            }

            usersUrl = Str(page, "@odata.nextLink") is { } link ? new Uri(link) : null;
        }

        return people;
    }

    private async Task<IReadOnlyList<DirectoryPerson>> LdapPeopleAsync(LdapSettings ldap, string? credential, CancellationToken cancellationToken)
    {
        using var connection = await LdapConnectAsync(ldap, credential, cancellationToken);
        var a = ldap.Attributes;
        string[] attributes = [a.Id, a.Email, a.Name, a.Department, a.Manager, a.Phone, a.EmployeeId, a.MemberOf, "userAccountControl"];
        List<LdapEntry> entries;
        try
        {
            entries = await connection.SearchUsingSimplePagingAsync(new SearchOptions(ldap.BaseDn, LdapConnection.ScopeSub, ldap.UserFilter, attributes), 500, cancellationToken);
        }
        catch (LdapException ex)
        {
            throw new ConnectorException("refused", $"The directory refused the search: {ex.ResultCode} {ex.Message}", ex.ResultCode is LdapException.NoSuchObject or LdapException.FilterError, ex);
        }

        // Managers are named by distinguished name; people by their stable id.
        var byDn = entries.ToDictionary(e => e.Dn, e => LdapId(e, a.Id), StringComparer.OrdinalIgnoreCase);
        var mapped = ldap.GroupMappings.Select(m => m.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. entries.Take(_options.MaxRecords).Select(e => LdapPerson(e, a, ldap.ActiveDirectory, byDn, mapped))];
    }

    /// <summary>A person from an LDAP entry (attribute names per the settings; disabled from Active Directory's <c>userAccountControl</c>).</summary>
    internal static DirectoryPerson LdapPerson(LdapEntry entry, LdapAttributes a, bool activeDirectory, IReadOnlyDictionary<string, string> idByDn, IReadOnlySet<string> mappedGroups)
    {
        string? Get(string name) => entry.TryGet(name, out var attribute) && attribute!.StringValue is { Length: > 0 } value ? value : null;
        var manager = Get(a.Manager) is { } managerDn && idByDn.TryGetValue(managerDn, out var managerId) ? managerId : null;
        var disabled = activeDirectory && int.TryParse(Get("userAccountControl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags) && (flags & 2) != 0;
        var groups = entry.TryGet(a.MemberOf, out var memberOf) ? memberOf!.StringValueArray.Where(mappedGroups.Contains).ToArray() : [];
        return new DirectoryPerson(LdapId(entry, a.Id), Get(a.Email), Get(a.Name), Get(a.Department), manager, Get(a.Phone), Get(a.EmployeeId), !disabled, groups);
    }

    // objectGUID is binary in Active Directory; other ids are text.
    private static string LdapId(LdapEntry entry, string attribute) =>
        entry.TryGet(attribute, out var value)
            ? attribute.Equals("objectGUID", StringComparison.OrdinalIgnoreCase) && value!.ByteValue is { Length: 16 } bytes ? new Guid(bytes).ToString() : value!.StringValue
            : entry.Dn;

    private async Task<LdapConnection> LdapConnectAsync(LdapSettings ldap, string? password, CancellationToken cancellationToken)
    {
        // The policy decides before Zelvo connects: the library opens its own socket.
        var addresses = IPAddress.TryParse(ldap.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(ldap.Host, cancellationToken);
        if (addresses.Length == 0 || !addresses.All(Policy.IsAllowed))
        {
            throw new ConnectorException("address-not-allowed",
                $"{ldap.Host} is on a network Zelvo may not reach. The operator can allow it (Connectors:Network:AllowedPrivateNetworks).", true);
        }

        var connection = new LdapConnection { SecureSocketLayer = ldap.Security == LdapSecurity.Ldaps, ConnectionTimeout = (int)_options.Timeout.TotalMilliseconds };
        try
        {
            await connection.ConnectAsync(ldap.Host, ldap.Port, cancellationToken);
            if (ldap.Security == LdapSecurity.StartTls)
            {
                await connection.StartTlsAsync(cancellationToken);
            }

            await connection.BindAsync(ldap.BindDn, Require(password, "password"), cancellationToken);
            return connection;
        }
        catch (LdapException ex)
        {
            connection.Dispose();
            throw new ConnectorException(ex.ResultCode == LdapException.InvalidCredentials ? "sign-in-failed" : "unreachable", $"{ldap.Host}: {ex.ResultCode} {ex.Message}",
                ex.ResultCode == LdapException.InvalidCredentials, ex);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            connection.Dispose();
            throw new ConnectorException("unreachable", $"{ldap.Host} could not be reached: {ex.Message}", false, ex);
        }
    }

    private Task<string> DynamicsTokenAsync(Connector connector, CrmSettings crm, string? credential, ExchangeLog log, CancellationToken cancellationToken) =>
        MicrosoftTokenAsync(connector.Id, Require(crm.DirectoryId, "directory id"), Require(crm.ClientId, "client id"), credential, new Uri(Base(crm.Url), ".default").AbsoluteUri, log,
            cancellationToken);

    private async Task<string> MicrosoftTokenAsync(Guid connectorId, string directory, string clientId, string? secret, string scope, ExchangeLog log, CancellationToken cancellationToken)
    {
        var key = $"{connectorId:N}|{scope}";
        if (_tokens.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Token;
        }

        var answer = await FormAsync(new Uri(_options.MicrosoftLoginUrl, $"{Uri.EscapeDataString(directory)}/oauth2/v2.0/token"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = Require(secret, "client secret"), ["scope"] = scope,
        }, log, cancellationToken);
        return Remember(key, answer, null);
    }

    private async Task<(string Token, Uri Instance)> SalesforceTokenAsync(Connector connector, CrmSettings crm, string? secret, ExchangeLog log, CancellationToken cancellationToken)
    {
        var key = $"{connector.Id:N}|salesforce";
        if (_tokens.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return (cached.Token, new Uri(cached.Instance!));
        }

        // The client credentials flow of a Salesforce connected app (its run-as user acts for Zelvo).
        var answer = await FormAsync(new Uri(Base(crm.Url), "services/oauth2/token"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials", ["client_id"] = Require(crm.ClientId, "client id"), ["client_secret"] = Require(secret, "client secret"),
        }, log, cancellationToken);
        var instance = Str(answer, "instance_url") is { } url ? Base(url) : Base(crm.Url);
        // Only the connected org's own address: answers from elsewhere are not followed.
        if (!string.Equals(Base(crm.Url).Host, instance.Host, StringComparison.OrdinalIgnoreCase) && !instance.Host.EndsWith(".salesforce.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorException("bad-answer", "Salesforce named an instance outside salesforce.com.", true);
        }

        var token = Remember(key, answer, instance.AbsoluteUri);
        return (token, instance);
    }

    private async Task<string> ZohoTokenAsync(Connector connector, CrmSettings crm, string? credential, ExchangeLog log, CancellationToken cancellationToken)
    {
        var key = $"{connector.Id:N}|zoho";
        if (_tokens.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Token;
        }

        var secrets = ZohoCredential.Read(credential);
        var answer = await FormAsync(new Uri(Base(crm.Url), "oauth/v2/token"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token", ["client_id"] = Require(crm.ClientId, "client id"), ["client_secret"] = secrets.ClientSecret, ["refresh_token"] = secrets.RefreshToken,
        }, log, cancellationToken);
        return Remember(key, answer, null);
    }

    private async Task<string> RestTokenAsync(Connector connector, RestSettings settings, string? secret, ExchangeLog log, CancellationToken cancellationToken)
    {
        var key = $"{connector.Id:N}|rest";
        if (_tokens.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Token;
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials", ["client_id"] = Require(settings.ClientId, "client id"), ["client_secret"] = Require(secret, "client secret"),
        };
        if (settings.Scope is { Length: > 0 } scope)
        {
            form["scope"] = scope;
        }

        return Remember(key, await FormAsync(new Uri(Require(settings.TokenUrl, "token address")), form, log, cancellationToken), null);
    }

    private string Remember(string key, JsonNode answer, string? instance)
    {
        var token = Str(answer, "access_token") ?? throw new ConnectorException("bad-answer", "No access token in the answer.");
        var seconds = answer["expires_in"] is JsonValue e && int.TryParse(e.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 1800;
        _tokens[key] = (token, instance, DateTimeOffset.UtcNow.AddSeconds(seconds));
        return token;
    }

    private async Task<JsonNode> FormAsync(Uri url, Dictionary<string, string> form, ExchangeLog log, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        // The token request's secrets never reach the log: only the field names.
        var (status, _, body, _) = await SendAsync(request, string.Join('&', form.Keys.Select(k => $"{k}=…")), log, cancellationToken);
        return Parse(status, body, url.Host);
    }

    private async Task<JsonNode> JsonAsync(
        HttpMethod method, Uri url, string token, JsonObject? body, ExchangeLog log, CancellationToken cancellationToken, (string Name, string Value)[]? headers = null,
        string scheme = "Bearer")
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var text = body?.ToJsonString();
        if (text is not null)
        {
            request.Content = new StringContent(text, Encoding.UTF8, "application/json");
        }

        var (status, _, answer, _) = await SendAsync(request, text, log, cancellationToken);
        return Parse(status, answer, url.Host);
    }

    private static JsonNode Parse(int status, string body, string host)
    {
        if (status is < 200 or >= 300)
        {
            throw Error(status, host, body);
        }

        try
        {
            return string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new ConnectorException("bad-answer", $"{host}: the answer is not JSON.", false, ex);
        }
    }

    private async Task<(int Status, string? Type, string Body, int Duration)> SendAsync(
        HttpRequestMessage request, string? requestBody, ExchangeLog log, CancellationToken cancellationToken, HttpClient? client = null)
    {
        if (request.RequestUri is not { } url || url.Scheme != Uri.UriSchemeHttps && !_options.Network.AllowInsecureHttp)
        {
            throw new ConnectorException("url-not-https", "Connected systems are reached over HTTPS only.", true);
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await (client ?? _http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var body = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxBody));
            var type = response.Content.Headers.ContentType?.MediaType;
            var duration = (int)watch.ElapsedMilliseconds;
            log.Exchanges.Add(new HttpExchange(request.Method.Method, url.AbsoluteUri, (int)response.StatusCode, duration, requestBody, request.Content?.Headers.ContentType?.MediaType,
                body, type, null));
            return ((int)response.StatusCode, type, body, duration);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OutboundRequestBlockedException && !cancellationToken.IsCancellationRequested)
        {
            var blocked = ex as OutboundRequestBlockedException ?? ex.InnerException as OutboundRequestBlockedException;
            var failure = blocked is not null
                ? new ConnectorException(blocked.Code, blocked.Message, true, ex)
                : ex is TaskCanceledException
                    ? new ConnectorException("timeout", $"{url.Host} did not answer in time.", false, ex)
                    : new ConnectorException("unreachable", $"{url.Host} could not be reached: {ex.Message}", false, ex);
            log.Exchanges.Add(new HttpExchange(request.Method.Method, url.AbsoluteUri, null, (int)watch.ElapsedMilliseconds, requestBody, null, null, null, failure.Summary));
            throw failure;
        }
    }

    /// <summary>The error of a refused call: refusals of the request are permanent; sign-in, limits and server errors are tried again.</summary>
    internal static ConnectorException Error(int status, string host, string body)
    {
        var detail = body.Length <= 300 ? body : body[..300];
        var code = status switch
        {
            401 => "sign-in-failed",
            403 => "access-denied",
            404 => "not-found",
            408 or 429 => "rate-limited",
            >= 500 => "provider-error",
            _ => "refused",
        };
        return new ConnectorException(code, $"{host}: {status} {detail}".Trim(), code is "refused" or "not-found" or "access-denied");
    }

    private static JsonObject Association(string id, int type) => new()
    {
        ["to"] = new JsonObject { ["id"] = id },
        ["types"] = new JsonArray(new JsonObject { ["associationCategory"] = "HUBSPOT_DEFINED", ["associationTypeId"] = type }),
    };

    private static Uri Base(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? (parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/")) : throw NotSetUp();

    private static string? Str(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Require(string? value, string what) =>
        string.IsNullOrEmpty(value) ? throw new ConnectorException("not-set-up", $"The connector has no {what}.", true) : value;

    private static ConnectorException NotSetUp() => new("not-set-up", "The connector is not fully set up.", true);

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

/// <summary>Zoho's credential: the client secret and the refresh token of the administrator's grant, stored together.</summary>
/// <param name="ClientSecret">Client secret.</param>
/// <param name="RefreshToken">Refresh token (from Zoho's self client, scope ZohoCRM.modules.ALL).</param>
public sealed record ZohoCredential(string ClientSecret, string RefreshToken)
{
    /// <summary>Reads the stored credential.</summary>
    public static ZohoCredential Read(string? stored)
    {
        try
        {
            return stored is null ? throw new ConnectorException("not-set-up", "The connector has no credential.", true)
                : JsonSerializer.Deserialize<ZohoCredential>(stored, Json)
                    ?? throw new ConnectorException("not-set-up", "The connector has no credential.", true);
        }
        catch (JsonException ex)
        {
            throw new ConnectorException("not-set-up", "The stored Zoho credential is not readable. Enter it again.", true, ex);
        }
    }

    /// <summary>As stored.</summary>
    public string Write() => JsonSerializer.Serialize(this, Json);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

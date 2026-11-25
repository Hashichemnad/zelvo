using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zelvo.Data.Connectors;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;

namespace Zelvo.Connectors;

/// <summary>A group of the connected directory and the Zelvo group its members join.</summary>
/// <param name="ExternalId">Entra group object id, or the LDAP group's distinguished name.</param>
/// <param name="GroupId">The Zelvo group.</param>
public sealed record GroupMapping(string ExternalId, Guid GroupId);

/// <summary>Microsoft Entra ID through Microsoft Graph (application permissions User.Read.All, GroupMember.Read.All).</summary>
public sealed record EntraSettings
{
    /// <summary>The Entra directory (tenant) id or domain.</summary>
    public string DirectoryId { get; init; } = string.Empty;

    /// <summary>The app registration's client id.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Only members of this group are synced (all users when empty).</summary>
    public string? UsersGroupId { get; init; }

    /// <summary>Entra groups whose members join Zelvo groups.</summary>
    public IReadOnlyList<GroupMapping> GroupMappings { get; init; } = [];

    /// <summary>Also offer "Sign in with Microsoft" on the sign-in page (Keycloak identity provider).</summary>
    public bool SignIn { get; init; }
}

/// <summary>How Zelvo protects the LDAP connection.</summary>
public enum LdapSecurity
{
    /// <summary>LDAPS (TLS from the start, port 636).</summary>
    Ldaps,

    /// <summary>StartTLS on the plain port (389), required.</summary>
    StartTls,

    /// <summary>No TLS (only inside a trusted network; the bind password travels in clear).</summary>
    None,
}

/// <summary>Which LDAP attributes hold what.</summary>
public sealed record LdapAttributes
{
    /// <summary>Stable id (<c>objectGUID</c> in Active Directory, <c>entryUUID</c> elsewhere).</summary>
    public string Id { get; init; } = "objectGUID";

    /// <summary>Email.</summary>
    public string Email { get; init; } = "mail";

    /// <summary>Display name.</summary>
    public string Name { get; init; } = "displayName";

    /// <summary>Department.</summary>
    public string Department { get; init; } = "department";

    /// <summary>Manager (a distinguished name).</summary>
    public string Manager { get; init; } = "manager";

    /// <summary>Mobile phone.</summary>
    public string Phone { get; init; } = "mobile";

    /// <summary>Employee number.</summary>
    public string EmployeeId { get; init; } = "employeeID";

    /// <summary>Group memberships (distinguished names).</summary>
    public string MemberOf { get; init; } = "memberOf";
}

/// <summary>Active Directory or another LDAP directory.</summary>
public sealed record LdapSettings
{
    /// <summary>Host name.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Port (636 for LDAPS, 389 otherwise).</summary>
    public int Port { get; init; } = 636;

    /// <summary>TLS.</summary>
    public LdapSecurity Security { get; init; } = LdapSecurity.Ldaps;

    /// <summary>Read-only service account's distinguished name; its password is the credential.</summary>
    public string BindDn { get; init; } = string.Empty;

    /// <summary>Where people are, for example <c>OU=Staff,DC=entity,DC=gov,DC=ae</c>.</summary>
    public string BaseDn { get; init; } = string.Empty;

    /// <summary>Which entries are people.</summary>
    public string UserFilter { get; init; } = "(&(objectCategory=person)(objectClass=user))";

    /// <summary>Active Directory (disabled accounts read from <c>userAccountControl</c>) or another directory.</summary>
    public bool ActiveDirectory { get; init; } = true;

    /// <summary>Attribute names.</summary>
    public LdapAttributes Attributes { get; init; } = new();

    /// <summary>Directory groups whose members join Zelvo groups.</summary>
    public IReadOnlyList<GroupMapping> GroupMappings { get; init; } = [];

    /// <summary>Also let people sign in with their directory password (Keycloak's read-only LDAP federation).</summary>
    public bool SignIn { get; init; }
}

/// <summary>A CRM: where it is, how Zelvo signs in, and how ticket states map to its case status.</summary>
public sealed record CrmSettings
{
    /// <summary>
    /// Dynamics: the environment's address (<c>https://entity.crm4.dynamics.com</c>); Salesforce: the
    /// sign-in address (My Domain, <c>https://entity.my.salesforce.com</c>); Zoho: the accounts
    /// server of the data centre (<c>https://accounts.zoho.com</c>). Not used for HubSpot.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>Zoho: the API domain of the data centre (<c>https://www.zohoapis.com</c>).</summary>
    public string? ApiUrl { get; init; }

    /// <summary>Dynamics: the Entra directory (tenant) id.</summary>
    public string? DirectoryId { get; init; }

    /// <summary>Dynamics, Salesforce and Zoho: the app's client id.</summary>
    public string? ClientId { get; init; }

    /// <summary>The CRM's case status per Zelvo ticket state; states left out use Zelvo's defaults for the CRM.</summary>
    public IReadOnlyDictionary<string, string> StatusMap { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Contacts' companies become customer groups (SLA policies by customer, B3).</summary>
    public bool CustomerGroups { get; init; } = true;

    /// <summary>Default case statuses of each CRM.</summary>
    public static IReadOnlyDictionary<string, string> DefaultStatuses(ConnectorKind kind) => kind switch
    {
        // Dataverse incident.statuscode: 1 In progress, 2 On hold, 3 Waiting for details, 5 Problem solved (closes the case).
        ConnectorKind.Dynamics365 => Map("1", "1", "1", "3", "5", "5", "1"),
        ConnectorKind.Salesforce => Map("New", "Working", "Working", "Working", "Closed", "Closed", "Working"),
        // HubSpot's default support pipeline: 1 New, 2 Waiting on contact, 3 Waiting on us, 4 Closed.
        ConnectorKind.HubSpot => Map("1", "1", "3", "2", "4", "4", "3"),
        ConnectorKind.Zoho => Map("New", "New", "Escalated", "On Hold", "Closed", "Closed", "Escalated"),
        _ => new Dictionary<string, string>(StringComparer.Ordinal),
    };

    /// <summary>The CRM's status for <paramref name="state"/>.</summary>
    public string StatusFor(ConnectorKind kind, TicketState state) =>
        StatusMap.TryGetValue(state.ToString(), out var value) ? value : DefaultStatuses(kind)[state.ToString()];

    private static Dictionary<string, string> Map(string @new, string assigned, string inProgress, string onHold, string resolved, string closed, string reopened) =>
        new(StringComparer.Ordinal)
        {
            [nameof(TicketState.New)] = @new,
            [nameof(TicketState.Assigned)] = assigned,
            [nameof(TicketState.InProgress)] = inProgress,
            [nameof(TicketState.OnHold)] = onHold,
            [nameof(TicketState.Resolved)] = resolved,
            [nameof(TicketState.Closed)] = closed,
            [nameof(TicketState.Reopened)] = reopened,
        };
}

/// <summary>How a REST connector authenticates.</summary>
public enum RestAuth
{
    /// <summary>None.</summary>
    None,

    /// <summary>The credential in a header (<see cref="RestSettings.AuthHeader"/>).</summary>
    ApiKey,

    /// <summary>HTTP Basic with <see cref="RestSettings.Username"/> and the credential.</summary>
    Basic,

    /// <summary><c>Authorization: Bearer</c> with the credential.</summary>
    Bearer,

    /// <summary>OAuth 2.0 client credentials (<see cref="RestSettings.TokenUrl"/>, client id and the credential as secret).</summary>
    OAuth2,
}

/// <summary>A value read from an operation's answer.</summary>
/// <param name="Key">Key (letters and digits).</param>
/// <param name="Name">Name shown in the ticket's note.</param>
/// <param name="Path">Dotted path in the JSON answer, for example <c>data.case.status</c>.</param>
public sealed record RestResultField(string Key, LocalizedText Name, string Path);

/// <summary>One call a workflow rule can make: method, path and body with placeholders, and what to read from the answer.</summary>
public sealed record RestOperation
{
    /// <summary>Key the rules name.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Name shown in the rule builder and the ticket's note.</summary>
    public LocalizedText Name { get; init; } = new(null, null);

    /// <summary>GET, POST, PUT, PATCH or DELETE.</summary>
    public string Method { get; init; } = "POST";

    /// <summary>Path and query after the base address, with placeholders (URL-encoded), for example <c>/cases/{{ticket.externalRef}}</c>.</summary>
    public string Path { get; init; } = "/";

    /// <summary>JSON body with placeholders (JSON-escaped); none for GET and DELETE.</summary>
    public string? Body { get; init; }

    /// <summary>Values read from the answer and written in an internal note on the ticket.</summary>
    public IReadOnlyList<RestResultField> Results { get; init; } = [];

    /// <summary>Dotted path of the other system's id of the record, kept as the ticket's external reference (<c>{{ticket.externalRef}}</c>).</summary>
    public string? ExternalRefPath { get; init; }
}

/// <summary>A REST API set up on screen (the no-code connector builder).</summary>
public sealed partial record RestSettings
{
    /// <summary>Base address (HTTPS).</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Authentication.</summary>
    public RestAuth Auth { get; init; } = RestAuth.None;

    /// <summary>Header name for <see cref="RestAuth.ApiKey"/>.</summary>
    public string? AuthHeader { get; init; }

    /// <summary>User name for Basic.</summary>
    public string? Username { get; init; }

    /// <summary>OAuth 2.0: the token endpoint.</summary>
    public string? TokenUrl { get; init; }

    /// <summary>OAuth 2.0: the client id.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth 2.0: the scope, if any.</summary>
    public string? Scope { get; init; }

    /// <summary>Headers sent with every call (no secrets: those go in the credential).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The operations.</summary>
    public IReadOnlyList<RestOperation> Operations { get; init; } = [];

    /// <summary>Placeholders operations may use.</summary>
    public static IReadOnlyList<string> Placeholders { get; } =
    [
        "ticket.id", "ticket.number", "ticket.title", "ticket.description", "ticket.state", "ticket.type", "ticket.priority", "ticket.channel",
        "ticket.group", "ticket.category", "ticket.url", "ticket.createdAt", "ticket.resolutionNotes", "ticket.externalRef",
        "requester.name", "requester.email", "requester.phone", "requester.employeeId", "tenant.name",
    ];

    /// <summary>What is wrong with these settings (field, message).</summary>
    public IEnumerable<(string Field, string Message)> Problems()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query))
        {
            yield return ("rest.baseUrl", "Enter the API's base address, starting with https://, without a query.");
        }

        switch (Auth)
        {
            case RestAuth.ApiKey when string.IsNullOrWhiteSpace(AuthHeader) || !HeaderName().IsMatch(AuthHeader):
                yield return ("rest.authHeader", "Enter the header's name, for example X-API-Key.");
                break;
            case RestAuth.Basic when string.IsNullOrWhiteSpace(Username):
                yield return ("rest.username", "Basic authentication needs a user name.");
                break;
            case RestAuth.OAuth2 when !Uri.TryCreate(TokenUrl, UriKind.Absolute, out var token) || token.Scheme != "https" || string.IsNullOrWhiteSpace(ClientId):
                yield return ("rest.tokenUrl", "OAuth needs the token address (https://) and the client id.");
                break;
        }

        foreach (var (name, value) in Headers)
        {
            if (!HeaderName().IsMatch(name) || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
                || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                yield return ("rest.headers", $"The header {name} is not allowed; secrets go in the credential.");
            }
        }

        if (Operations.Count is 0 or > 30)
        {
            yield return ("rest.operations", "Add 1 to 30 operations.");
        }

        if (Operations.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != Operations.Count)
        {
            yield return ("rest.operations", "Each operation needs its own key.");
        }

        for (var i = 0; i < Operations.Count; i++)
        {
            var operation = Operations[i];
            var at = $"rest.operations.{i}";
            if (!KeyPattern().IsMatch(operation.Key))
            {
                yield return ($"{at}.key", "A key has letters, digits and hyphens, starting with a letter.");
            }

            if (!operation.Name.HasAnyValue)
            {
                yield return ($"{at}.name", "Name the operation in English, Arabic or both.");
            }

            if (operation.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            {
                yield return ($"{at}.method", "The method is GET, POST, PUT, PATCH or DELETE.");
            }

            if (!operation.Path.StartsWith('/') || operation.Path.StartsWith("//", StringComparison.Ordinal) || operation.Path.Contains("://", StringComparison.Ordinal)
                || operation.Path.Contains('\\', StringComparison.Ordinal) || operation.Path.Contains("..", StringComparison.Ordinal))
            {
                yield return ($"{at}.path", "The path starts with / and stays on the base address.");
            }

            foreach (var placeholder in PlaceholdersIn(operation.Path).Concat(PlaceholdersIn(operation.Body)).Distinct(StringComparer.Ordinal))
            {
                if (!Placeholders.Contains(placeholder, StringComparer.Ordinal))
                {
                    yield return ($"{at}.placeholders", $"Unknown placeholder {{{{{placeholder}}}}}.");
                }
            }

            if (operation.Body is { Length: > 8000 })
            {
                yield return ($"{at}.body", "The body is at most 8,000 characters.");
            }

            if (operation.Results.Count > 20 || operation.Results.Any(r => !KeyPattern().IsMatch(r.Key) || string.IsNullOrWhiteSpace(r.Path) || !r.Name.HasAnyValue))
            {
                yield return ($"{at}.results", "Each value read from the answer needs a key, a name and a path (at most 20).");
            }
        }
    }

    /// <summary>The placeholder names in <paramref name="template"/>.</summary>
    public static IEnumerable<string> PlaceholdersIn(string? template) =>
        template is null ? [] : PlaceholderPattern().Matches(template).Select(m => m.Groups[1].Value);

    /// <summary>Puts <paramref name="values"/> into <paramref name="template"/>, each escaped by <paramref name="escape"/>.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values, Func<string, string> escape)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(escape);
        return PlaceholderPattern().Replace(template, m => escape(values.GetValueOrDefault(m.Groups[1].Value) ?? string.Empty));
    }

    [GeneratedRegex(@"\{\{\s*([a-zA-Z][a-zA-Z.]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderName();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9-]{0,39}$")]
    private static partial Regex KeyPattern();
}

/// <summary>A connector's settings as stored (one of them, by kind).</summary>
public sealed record ConnectorSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Entra ID.</summary>
    public EntraSettings? Entra { get; init; }

    /// <summary>LDAP.</summary>
    public LdapSettings? Ldap { get; init; }

    /// <summary>CRMs.</summary>
    public CrmSettings? Crm { get; init; }

    /// <summary>REST.</summary>
    public RestSettings? Rest { get; init; }

    /// <summary>JSON options of settings.</summary>
    public static JsonSerializerOptions JsonOptions => Json;

    /// <summary>Reads stored settings (empty when unreadable).</summary>
    public static ConnectorSettings Read(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<ConnectorSettings>(json, Json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>As stored.</summary>
    public string Write() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Kinds of connectors by what they do.</summary>
public static class ConnectorKinds
{
    /// <summary>Identity directories.</summary>
    public static bool IsDirectory(ConnectorKind kind) => kind is ConnectorKind.EntraId or ConnectorKind.Ldap;

    /// <summary>CRMs.</summary>
    public static bool IsCrm(ConnectorKind kind) => kind is ConnectorKind.Dynamics365 or ConnectorKind.Salesforce or ConnectorKind.HubSpot or ConnectorKind.Zoho;

    /// <summary>Whether the connector syncs on a schedule.</summary>
    public static bool Syncs(ConnectorKind kind) => IsDirectory(kind) || IsCrm(kind);
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Novell.Directory.Ldap;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Connectors;

namespace Zelvo.Connectors;

/// <summary>Software a source found on a record.</summary>
public sealed record DiscoveredSoftware(string Name, string? Publisher, string? Version);

/// <summary>A relationship a source reports: the record <paramref name="Type"/> (a relationship type key) the record <paramref name="Target"/> (an external id of the same source).</summary>
public sealed record DiscoveredLink(string Type, string Target);

/// <summary>
/// A configuration item as a source reports it (task G2): the source's id, a class key (Zelvo's
/// standard keys unless the source maps them), a name and attribute values by key.
/// </summary>
public sealed record DiscoveredRecord(
    string ExternalId, string ClassKey, string Name, JsonObject Attributes, string? PrimaryUser = null,
    IReadOnlyList<DiscoveredSoftware>? Software = null, IReadOnlyList<DiscoveredLink>? Links = null);

/// <summary>Settings of a discovery source (task G2), by kind; stored as JSON.</summary>
public sealed partial record DiscoverySettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Azure resource types read by default.</summary>
    public static readonly IReadOnlyList<string> DefaultResourceTypes =
    [
        "microsoft.compute/virtualmachines", "microsoft.sql/servers/databases", "microsoft.dbforpostgresql/flexibleservers",
        "microsoft.dbformysql/flexibleservers", "microsoft.storage/storageaccounts", "microsoft.web/sites",
    ];

    /// <summary>Zelvo's class key → the tenant's class key, for tenants that named their classes otherwise.</summary>
    public IReadOnlyDictionary<string, string> ClassMap { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Intune: also read the detected apps of each device (for licence compliance).</summary>
    public bool IncludeApps { get; init; } = true;

    /// <summary>Azure: the subscriptions to read (all the app may read when empty).</summary>
    public IReadOnlyList<string> SubscriptionIds { get; init; } = [];

    /// <summary>Azure: the resource types to read.</summary>
    public IReadOnlyList<string> ResourceTypes { get; init; } = DefaultResourceTypes;

    /// <summary>Active Directory: where computers are (the connector's base DN when empty).</summary>
    public string? BaseDn { get; init; }

    /// <summary>Active Directory: which objects are computers.</summary>
    public string ComputerFilter { get; init; } = "(objectCategory=computer)";

    /// <summary>vCenter: its address (HTTPS).</summary>
    public string? Url { get; init; }

    /// <summary>vCenter: the read-only account.</summary>
    public string? Username { get; init; }

    /// <summary>vCenter: the SHA-256 fingerprint of its certificate, for servers whose certificate is not trusted (offline networks).</summary>
    public string? CertificateThumbprint { get; init; }

    /// <summary>The settings in <paramref name="json"/> (defaults when unreadable).</summary>
    public static DiscoverySettings Read(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<DiscoverySettings>(json, Json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>As stored.</summary>
    public string Write() => JsonSerializer.Serialize(this, Json);

    /// <summary>The class key a record of Zelvo's <paramref name="key"/> gets.</summary>
    public string MapClass(string key) => ClassMap.TryGetValue(key, out var mapped) && mapped.Length > 0 ? mapped : key;

    /// <summary>What is wrong with the settings of a source of <paramref name="kind"/> (field, code).</summary>
    public List<(string Field, string Code)> Problems(DiscoverySourceKind kind)
    {
        var problems = new List<(string, string)>();
        if (ClassMap.Count > 50 || ClassMap.Any(m => !ClassKeyPattern().IsMatch(m.Key) || !ClassKeyPattern().IsMatch(m.Value)))
        {
            problems.Add(("classMap", "class-map-invalid"));
        }

        switch (kind)
        {
            case DiscoverySourceKind.Azure:
                if (SubscriptionIds.Count > 100 || SubscriptionIds.Any(s => !Guid.TryParse(s, out _)))
                {
                    problems.Add(("subscriptionIds", "subscription-invalid"));
                }

                if (ResourceTypes.Count is 0 or > 50 || ResourceTypes.Any(t => !ResourceTypePattern().IsMatch(t)))
                {
                    problems.Add(("resourceTypes", "resource-type-invalid"));
                }

                break;
            case DiscoverySourceKind.ActiveDirectory:
                if (ComputerFilter.Length is 0 or > 500 || !ComputerFilter.StartsWith('(') || !ComputerFilter.EndsWith(')')
                    || ComputerFilter.Count(c => c == '(') != ComputerFilter.Count(c => c == ')'))
                {
                    problems.Add(("computerFilter", "filter-invalid"));
                }

                if (BaseDn is { Length: > 500 })
                {
                    problems.Add(("baseDn", "base-dn-invalid"));
                }

                break;
            case DiscoverySourceKind.VCenter:
                if (!Uri.TryCreate(Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.Query))
                {
                    problems.Add(("url", "url-not-https"));
                }

                if (string.IsNullOrWhiteSpace(Username) || Username.Length > 200)
                {
                    problems.Add(("username", "username-required"));
                }

                if (CertificateThumbprint is { Length: > 0 } print && !ThumbprintPattern().IsMatch(NormalizeThumbprint(print)))
                {
                    problems.Add(("certificateThumbprint", "thumbprint-invalid"));
                }

                break;
        }

        return problems;
    }

    /// <summary>Upper-case hex without separators.</summary>
    public static string NormalizeThumbprint(string value) => value.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ClassKeyPattern();

    [GeneratedRegex("^[a-z0-9.]+/[a-z0-9./]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ResourceTypePattern();

    [GeneratedRegex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ThumbprintPattern();
}

/// <summary>Reads discovery sources (task G2). Replaced in tests.</summary>
public interface IDiscoveryClients
{
    /// <summary>
    /// Everything the source reports now (a complete list). Intune, Entra ID devices and Azure use
    /// <paramref name="connector"/>'s Entra app and <paramref name="connectorCredential"/>; Active
    /// Directory its LDAP connection; vCenter <paramref name="credential"/> (its password).
    /// </summary>
    Task<IReadOnlyList<DiscoveredRecord>> DiscoverAsync(
        DiscoverySourceKind kind, DiscoverySettings settings, Connector? connector, ConnectorSettings? connectorSettings, string? connectorCredential, string? credential,
        ExchangeLog log, CancellationToken cancellationToken);
}

/// <summary>
/// Zelvo's own thin clients for the discovery sources: Microsoft Graph (Intune managed devices and
/// detected apps, Entra ID devices), Azure Resource Graph, Active Directory computers over LDAP and
/// the vCenter REST API, through the connectors' outgoing-request policy.
/// </summary>
public sealed partial class ConnectorClients : IDiscoveryClients
{
    /// <summary>At most this many detected apps are followed to their devices per Intune sync.</summary>
    internal const int MaxApps = 2000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredRecord>> DiscoverAsync(
        DiscoverySourceKind kind, DiscoverySettings settings, Connector? connector, ConnectorSettings? connectorSettings, string? connectorCredential, string? credential,
        ExchangeLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        switch (kind)
        {
            case DiscoverySourceKind.Intune:
            case DiscoverySourceKind.EntraDevices:
            case DiscoverySourceKind.Azure:
            {
                if (connector is not { Kind: ConnectorKind.EntraId } || connectorSettings?.Entra is not { } entra)
                {
                    throw new ConnectorException("connector-required", "Choose an Entra ID connector whose app Zelvo uses.", true);
                }

                return kind switch
                {
                    DiscoverySourceKind.Intune => await IntuneAsync(connector, entra, connectorCredential, settings, log, cancellationToken).ConfigureAwait(false),
                    DiscoverySourceKind.EntraDevices => await EntraDevicesAsync(connector, entra, connectorCredential, settings, log, cancellationToken).ConfigureAwait(false),
                    _ => await AzureAsync(connector, entra, connectorCredential, settings, log, cancellationToken).ConfigureAwait(false),
                };
            }

            case DiscoverySourceKind.ActiveDirectory:
                if (connector is not { Kind: ConnectorKind.Ldap } || connectorSettings?.Ldap is not { } ldap)
                {
                    throw new ConnectorException("connector-required", "Choose an Active Directory (LDAP) connector whose connection Zelvo uses.", true);
                }

                return await AdComputersAsync(ldap, connectorCredential, settings, cancellationToken).ConfigureAwait(false);

            case DiscoverySourceKind.VCenter:
                return await VCenterAsync(settings, credential, log, cancellationToken).ConfigureAwait(false);

            default:
                throw new ConnectorException("not-a-pull-source", "Tools push records to discovery API sources; Zelvo does not read them.", true);
        }
    }

    /// <summary>A MAC address as <c>AA:BB:CC:DD:EE:FF</c>, or <see langword="null"/> when it is not one.</summary>
    public static string? NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hex = new string([.. value.Where(Uri.IsHexDigit)]).ToUpperInvariant();
        return hex.Length == 12 && hex != "000000000000"
            ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)))
            : null;
    }

    private async Task<IReadOnlyList<DiscoveredRecord>> IntuneAsync(
        Connector connector, EntraSettings entra, string? credential, DiscoverySettings settings, ExchangeLog log, CancellationToken cancellationToken)
    {
        var token = await MicrosoftTokenAsync(connector.Id, entra.DirectoryId, entra.ClientId, credential, new Uri(_options.GraphUrl, ".default").AbsoluteUri, log, cancellationToken)
            .ConfigureAwait(false);
        var devices = new Dictionary<string, (string Class, string Name, JsonObject Attributes, string? User)>(StringComparer.Ordinal);
        Uri? next = new(_options.GraphUrl,
            "v1.0/deviceManagement/managedDevices?$select=id,deviceName,serialNumber,manufacturer,model,operatingSystem,osVersion,wiFiMacAddress,ethernetMacAddress,imei,userPrincipalName&$top=500");
        while (next is not null && devices.Count < _options.MaxRecords)
        {
            var page = await JsonAsync(HttpMethod.Get, next, token, null, log, cancellationToken).ConfigureAwait(false);
            foreach (var d in page["value"] as JsonArray ?? [])
            {
                if (Str(d, "id") is not { } id)
                {
                    continue;
                }

                var os = Str(d, "operatingSystem");
                var mobile = os is not null && (os.StartsWith("iOS", StringComparison.OrdinalIgnoreCase) || os.StartsWith("iPadOS", StringComparison.OrdinalIgnoreCase)
                    || os.StartsWith("Android", StringComparison.OrdinalIgnoreCase));
                var attributes = Attributes(
                    ("serialNumber", Str(d, "serialNumber")), ("manufacturer", Str(d, "manufacturer")), ("model", Str(d, "model")),
                    ("operatingSystem", Join(os, Str(d, "osVersion"))));
                if (mobile)
                {
                    Put(attributes, "imei", Str(d, "imei"));
                }
                else
                {
                    Put(attributes, "hostName", Str(d, "deviceName"));
                    Put(attributes, "macAddress", NormalizeMac(Str(d, "ethernetMacAddress")) ?? NormalizeMac(Str(d, "wiFiMacAddress")));
                }

                devices[id] = (mobile ? "mobile-device" : "computer", Str(d, "deviceName") ?? id, attributes, Str(d, "userPrincipalName"));
            }

            next = Str(page, "@odata.nextLink") is { } link ? new Uri(link) : null;
        }

        var software = new Dictionary<string, List<DiscoveredSoftware>>(StringComparer.Ordinal);
        if (settings.IncludeApps)
        {
            var apps = new List<(string Id, DiscoveredSoftware App)>();
            Uri? appsUrl = new(_options.GraphUrl, "v1.0/deviceManagement/detectedApps?$select=id,displayName,version,publisher&$top=500");
            while (appsUrl is not null && apps.Count < MaxApps)
            {
                var page = await JsonAsync(HttpMethod.Get, appsUrl, token, null, log, cancellationToken).ConfigureAwait(false);
                foreach (var a in page["value"] as JsonArray ?? [])
                {
                    if (Str(a, "id") is { } id && Str(a, "displayName") is { } name)
                    {
                        apps.Add((id, new DiscoveredSoftware(name, Str(a, "publisher"), Str(a, "version"))));
                    }
                }

                appsUrl = Str(page, "@odata.nextLink") is { } link ? new Uri(link) : null;
            }

            foreach (var (appId, app) in apps.Take(MaxApps))
            {
                Uri? onDevices = new(_options.GraphUrl, $"v1.0/deviceManagement/detectedApps/{Uri.EscapeDataString(appId)}/managedDevices?$select=id&$top=500");
                while (onDevices is not null)
                {
                    var page = await JsonAsync(HttpMethod.Get, onDevices, token, null, log, cancellationToken).ConfigureAwait(false);
                    foreach (var d in page["value"] as JsonArray ?? [])
                    {
                        if (Str(d, "id") is { } deviceId && devices.ContainsKey(deviceId))
                        {
                            (software.TryGetValue(deviceId, out var list) ? list : software[deviceId] = []).Add(app);
                        }
                    }

                    onDevices = Str(page, "@odata.nextLink") is { } link ? new Uri(link) : null;
                }
            }
        }

        return [.. devices.Select(d => new DiscoveredRecord(d.Key, settings.MapClass(d.Value.Class), d.Value.Name, d.Value.Attributes, d.Value.User,
            software.GetValueOrDefault(d.Key) ?? []))];
    }

    private async Task<IReadOnlyList<DiscoveredRecord>> EntraDevicesAsync(
        Connector connector, EntraSettings entra, string? credential, DiscoverySettings settings, ExchangeLog log, CancellationToken cancellationToken)
    {
        var token = await MicrosoftTokenAsync(connector.Id, entra.DirectoryId, entra.ClientId, credential, new Uri(_options.GraphUrl, ".default").AbsoluteUri, log, cancellationToken)
            .ConfigureAwait(false);
        var records = new List<DiscoveredRecord>();
        Uri? next = new(_options.GraphUrl, "v1.0/devices?$select=id,deviceId,displayName,operatingSystem,operatingSystemVersion,accountEnabled,manufacturer,model&$top=999");
        while (next is not null && records.Count < _options.MaxRecords)
        {
            var page = await JsonAsync(HttpMethod.Get, next, token, null, log, cancellationToken).ConfigureAwait(false);
            foreach (var d in page["value"] as JsonArray ?? [])
            {
                var enabled = d?["accountEnabled"] is not JsonValue v || !v.TryGetValue<bool>(out var on) || on;
                if ((Str(d, "deviceId") ?? Str(d, "id")) is not { } id || !enabled)
                {
                    continue;
                }

                var os = Str(d, "operatingSystem");
                var mobile = os is not null && (os.StartsWith("iOS", StringComparison.OrdinalIgnoreCase) || os.StartsWith("iPadOS", StringComparison.OrdinalIgnoreCase)
                    || os.StartsWith("Android", StringComparison.OrdinalIgnoreCase));
                var name = Str(d, "displayName") ?? id;
                var attributes = Attributes(("manufacturer", Str(d, "manufacturer")), ("model", Str(d, "model")), ("operatingSystem", Join(os, Str(d, "operatingSystemVersion"))));
                if (!mobile)
                {
                    Put(attributes, "hostName", name);
                }

                records.Add(new DiscoveredRecord(id, settings.MapClass(mobile ? "mobile-device" : "computer"), name, attributes));
            }

            next = Str(page, "@odata.nextLink") is { } link ? new Uri(link) : null;
        }

        return records;
    }

    private async Task<IReadOnlyList<DiscoveredRecord>> AzureAsync(
        Connector connector, EntraSettings entra, string? credential, DiscoverySettings settings, ExchangeLog log, CancellationToken cancellationToken)
    {
        var token = await MicrosoftTokenAsync(connector.Id, entra.DirectoryId, entra.ClientId, credential, new Uri(_options.ManagementUrl, ".default").AbsoluteUri, log, cancellationToken)
            .ConfigureAwait(false);
        // Types and subscriptions were checked when saved (letters, digits, dots and slashes; GUIDs).
        var types = string.Join(", ", settings.ResourceTypes.Select(t => $"'{t}'"));
        var query = $"""
            Resources
            | where type in~ ({types})
            | project id, name, type, location, computerName = tostring(properties.osProfile.computerName), osType = tostring(properties.storageProfile.osDisk.osType),
                vmSize = tostring(properties.hardwareProfile.vmSize)
            | order by id asc
            """;
        var url = new Uri(_options.ManagementUrl, "providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01");
        var records = new List<DiscoveredRecord>();
        string? skipToken = null;
        do
        {
            var options = new JsonObject { ["$top"] = 1000, ["resultFormat"] = "objectArray" };
            if (skipToken is not null)
            {
                options["$skipToken"] = skipToken;
            }

            var body = new JsonObject { ["query"] = query, ["options"] = options };
            if (settings.SubscriptionIds.Count > 0)
            {
                body["subscriptions"] = new JsonArray([.. settings.SubscriptionIds.Select(s => (JsonNode)JsonValue.Create(s))]);
            }

            var page = await JsonAsync(HttpMethod.Post, url, token, body, log, cancellationToken).ConfigureAwait(false);
            foreach (var r in page["data"] as JsonArray ?? [])
            {
                if (Str(r, "id") is not { } id)
                {
                    continue;
                }

                var type = Str(r, "type")?.ToLowerInvariant() ?? string.Empty;
                var classKey = type switch
                {
                    "microsoft.compute/virtualmachines" => "virtual-machine",
                    "microsoft.storage/storageaccounts" => "storage",
                    _ when type.Contains("database", StringComparison.Ordinal) || type.StartsWith("microsoft.dbfor", StringComparison.Ordinal)
                        || type.StartsWith("microsoft.documentdb", StringComparison.Ordinal) => "cloud-database",
                    _ => "cloud-resource",
                };
                var attributes = Attributes(("resourceId", id.ToLowerInvariant()), ("provider", "azure"), ("region", Str(r, "location")));
                if (classKey == "virtual-machine")
                {
                    Put(attributes, "hostName", Str(r, "computerName"));
                    Put(attributes, "operatingSystem", Str(r, "osType"));
                }

                records.Add(new DiscoveredRecord(id.ToLowerInvariant(), settings.MapClass(classKey), Str(r, "name") ?? id, attributes));
            }

            skipToken = Str(page, "$skipToken");
        }
        while (skipToken is not null && records.Count < _options.MaxRecords);

        return records;
    }

    private async Task<IReadOnlyList<DiscoveredRecord>> AdComputersAsync(LdapSettings ldap, string? password, DiscoverySettings settings, CancellationToken cancellationToken)
    {
        using var connection = await LdapConnectAsync(ldap, password, cancellationToken).ConfigureAwait(false);
        string[] attributes = [ldap.Attributes.Id, "name", "dNSHostName", "operatingSystem", "operatingSystemVersion", "userAccountControl"];
        List<LdapEntry> entries;
        try
        {
            entries = await connection.SearchUsingSimplePagingAsync(
                new SearchOptions(settings.BaseDn is { Length: > 0 } baseDn ? baseDn : ldap.BaseDn, LdapConnection.ScopeSub, settings.ComputerFilter, attributes), 500, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LdapException ex)
        {
            throw new ConnectorException("refused", $"The directory refused the search: {ex.ResultCode} {ex.Message}", ex.ResultCode is LdapException.NoSuchObject or LdapException.FilterError, ex);
        }

        return [.. entries.Take(_options.MaxRecords).Select(e => AdComputer(e, ldap.Attributes.Id, settings)).Where(r => r is not null).Select(r => r!)];
    }

    /// <summary>A computer from an Active Directory entry; <see langword="null"/> for disabled accounts.</summary>
    internal static DiscoveredRecord? AdComputer(LdapEntry entry, string idAttribute, DiscoverySettings settings)
    {
        string? Get(string name) => entry.TryGet(name, out var attribute) && attribute!.StringValue is { Length: > 0 } value ? value : null;
        if (int.TryParse(Get("userAccountControl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags) && (flags & 2) != 0)
        {
            return null;
        }

        var os = Get("operatingSystem");
        var name = Get("name") ?? entry.Dn;
        var classKey = os?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true ? "server" : "computer";
        return new DiscoveredRecord(LdapId(entry, idAttribute), settings.MapClass(classKey), name,
            Attributes(("hostName", Get("dNSHostName") ?? name), ("operatingSystem", Join(os, Get("operatingSystemVersion")))));
    }

    private async Task<IReadOnlyList<DiscoveredRecord>> VCenterAsync(DiscoverySettings settings, string? password, ExchangeLog log, CancellationToken cancellationToken)
    {
        var baseUrl = new Uri((settings.Url ?? throw NotSetUp()).TrimEnd('/') + "/");
        using var client = VCenterClient(settings.CertificateThumbprint);
        string session;
        using (var signIn = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "api/session")))
        {
            signIn.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{Require(password, "password")}")));
            var (status, _, body, _) = await SendAsync(signIn, null, log, cancellationToken, client).ConfigureAwait(false);
            // The answer is the session id: never kept in the log.
            log.Exchanges[^1] = log.Exchanges[^1] with { ResponseBody = null };
            session = Parse(status, body, baseUrl.Host) is JsonValue id && id.TryGetValue<string>(out var text) ? text : throw new ConnectorException("bad-answer", "vCenter gave no session.");
        }

        async Task<JsonArray> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
            request.Headers.TryAddWithoutValidation("vmware-api-session-id", session);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var (status, _, body, _) = await SendAsync(request, null, log, cancellationToken, client).ConfigureAwait(false);
            return Parse(status, body, baseUrl.Host) as JsonArray ?? [];
        }

        var records = new List<DiscoveredRecord>();
        try
        {
            var hostCluster = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cluster in await GetAsync("api/vcenter/cluster").ConfigureAwait(false))
            {
                if (Str(cluster, "cluster") is not { } id)
                {
                    continue;
                }

                var links = new List<DiscoveredLink>();
                foreach (var host in await GetAsync($"api/vcenter/host?clusters={Uri.EscapeDataString(id)}").ConfigureAwait(false))
                {
                    if (Str(host, "host") is { } hostId)
                    {
                        hostCluster[hostId] = id;
                        links.Add(new DiscoveredLink("depends-on", $"host:{hostId}"));
                    }
                }

                records.Add(new DiscoveredRecord($"cluster:{id}", settings.MapClass("virtualisation-cluster"), Str(cluster, "name") ?? id,
                    Attributes(("resourceId", $"vcenter:{baseUrl.Host}/{id}"), ("provider", "vmware")), Links: links));
            }

            foreach (var host in await GetAsync("api/vcenter/host").ConfigureAwait(false))
            {
                if (Str(host, "host") is not { } hostId)
                {
                    continue;
                }

                var name = Str(host, "name") ?? hostId;
                records.Add(new DiscoveredRecord($"host:{hostId}", settings.MapClass("server"), name, Attributes(("hostName", name))));
                foreach (var vm in await GetAsync($"api/vcenter/vm?hosts={Uri.EscapeDataString(hostId)}").ConfigureAwait(false))
                {
                    if (Str(vm, "vm") is not { } vmId || records.Count >= _options.MaxRecords)
                    {
                        continue;
                    }

                    var vmName = Str(vm, "name") ?? vmId;
                    var attributes = Attributes(("resourceId", $"vcenter:{baseUrl.Host}/{vmId}"), ("provider", "vmware"));
                    if (vm?["cpu_count"] is JsonValue cpu && cpu.TryGetValue<int>(out var cores) && cores > 0)
                    {
                        attributes["cpuCores"] = cores;
                    }

                    if (vm?["memory_size_MiB"] is JsonValue memory && memory.TryGetValue<long>(out var mib) && mib >= 1024)
                    {
                        attributes["memoryGb"] = Math.Round(mib / 1024m, 0);
                    }

                    // VMware Tools tells the guest's own host name and system; a powered-off VM or
                    // one without the tools answers an error, which is not a failure of the sync.
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, $"api/vcenter/vm/{Uri.EscapeDataString(vmId)}/guest/identity"));
                        request.Headers.TryAddWithoutValidation("vmware-api-session-id", session);
                        var (status, _, body, _) = await SendAsync(request, null, log, cancellationToken, client).ConfigureAwait(false);
                        if (status is >= 200 and < 300 && JsonNode.Parse(body) is { } guest)
                        {
                            Put(attributes, "hostName", Str(guest, "host_name"));
                            Put(attributes, "operatingSystem", Str(guest["full_name"], "default_message"));
                        }
                    }
                    catch (Exception ex) when (ex is ConnectorException { Permanent: false } or JsonException)
                    {
                        // Left without the guest's details.
                    }

                    attributes.TryAdd("hostName", vmName);
                    records.Add(new DiscoveredRecord($"vm:{vmId}", settings.MapClass("virtual-machine"), vmName, attributes,
                        Links: [new DiscoveredLink("runs-on", $"host:{hostId}")]));
                }
            }
        }
        finally
        {
            try
            {
                using var signOut = new HttpRequestMessage(HttpMethod.Delete, new Uri(baseUrl, "api/session"));
                signOut.Headers.TryAddWithoutValidation("vmware-api-session-id", session);
                await SendAsync(signOut, null, log, CancellationToken.None, client).ConfigureAwait(false);
            }
            catch (ConnectorException)
            {
                // The session ends by itself after a while.
            }
        }

        return records;
    }

    /// <summary>
    /// A client for one vCenter: the connectors' address policy, and its certificate accepted when
    /// it is trusted or when its SHA-256 fingerprint is the one an administrator entered.
    /// </summary>
    private HttpClient VCenterClient(string? thumbprint)
    {
        if (_testHandler is not null)
        {
            return new HttpClient(_testHandler, disposeHandler: false) { Timeout = _options.Timeout };
        }

        var pinned = thumbprint is { Length: > 0 } ? DiscoverySettings.NormalizeThumbprint(thumbprint) : null;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = (context, ct) => Policy.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || pinned is not null && certificate is not null && CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))), Encoding.ASCII.GetBytes(pinned)),
            },
        };
        var client = new HttpClient(handler) { Timeout = _options.Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Zelvo/1.0");
        return client;
    }

    private static JsonObject Attributes(params (string Key, string? Value)[] values)
    {
        var attributes = new JsonObject();
        foreach (var (key, value) in values)
        {
            Put(attributes, key, value);
        }

        return attributes;
    }

    private static void Put(JsonObject attributes, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !attributes.ContainsKey(key))
        {
            attributes[key] = value.Trim();
        }
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first} {second}";

}

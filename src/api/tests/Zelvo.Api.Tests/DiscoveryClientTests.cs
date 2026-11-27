using System.Net;
using System.Text;
using System.Text.Json;
using Novell.Directory.Ldap;
using Zelvo.Connectors;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Connectors;
using Zelvo.Integrations;

namespace Zelvo.Api.Tests;

/// <summary>The discovery clients (task G2): Intune, Entra ID devices, Azure Resource Graph, Active Directory computers and vCenter, against a recording HTTP handler.</summary>
public sealed class DiscoveryClientTests
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

    private static (Connector, ConnectorSettings) Entra()
    {
        var settings = new ConnectorSettings { Entra = new EntraSettings { DirectoryId = Guid.NewGuid().ToString(), ClientId = "app" } };
        return (new Connector { Name = "Entra", Kind = ConnectorKind.EntraId, Settings = settings.Write() }, settings);
    }

    [Fact]
    public async Task Intune_reads_managed_devices_as_computers_and_phones_with_their_detected_apps()
    {
        var recorder = new Recorder((request, _) => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/token", StringComparison.Ordinal) => Json(new { access_token = "graph-token", expires_in = 3600 }),
            var p when p.EndsWith("/managedDevices", StringComparison.Ordinal) && p.Contains("detectedApps", StringComparison.Ordinal) =>
                Json(new { value = new[] { new { id = "dev-1" }, new { id = "unknown" } } }),
            var p when p.EndsWith("/detectedApps", StringComparison.Ordinal) => Json(new { value = new[] { new { id = "app-1", displayName = "Microsoft Teams", version = "24.1", publisher = "Microsoft" } } }),
            _ => Json(new
            {
                value = new object[]
                {
                    new { id = "dev-1", deviceName = "LT-0042", serialNumber = "5CG1234", manufacturer = "HP", model = "EliteBook 840", operatingSystem = "Windows", osVersion = "10.0.22631",
                        ethernetMacAddress = "", wiFiMacAddress = "a1b2c3d4e5f6", userPrincipalName = "fatima@entity.example.ae" },
                    new { id = "dev-2", deviceName = "Fatima's iPhone", serialNumber = "F2LX", operatingSystem = "iOS", osVersion = "17.4", imei = "356938035643809" },
                },
            }),
        });
        var (connector, settings) = Entra();
        var log = new ExchangeLog();
        var records = await Clients(recorder).DiscoverAsync(DiscoverySourceKind.Intune, new DiscoverySettings(), connector, settings, "app-secret", null, log, Ct);

        var laptop = records.Single(r => r.ExternalId == "dev-1");
        Assert.Equal("computer", laptop.ClassKey);
        Assert.Equal("A1:B2:C3:D4:E5:F6", (string)laptop.Attributes["macAddress"]!);
        Assert.Equal("Windows 10.0.22631", (string)laptop.Attributes["operatingSystem"]!);
        Assert.Equal("fatima@entity.example.ae", laptop.PrimaryUser);
        Assert.Equal("Microsoft Teams", Assert.Single(laptop.Software!).Name);
        var phone = records.Single(r => r.ExternalId == "dev-2");
        Assert.Equal("mobile-device", phone.ClassKey);
        Assert.Equal("356938035643809", (string)phone.Attributes["imei"]!);
        Assert.False(phone.Attributes.ContainsKey("hostName"));
        Assert.All(recorder.Requests.Where(r => !r.Url.Contains("/token", StringComparison.Ordinal)), r => Assert.Equal("Bearer graph-token", r.Authorization));
        // Without apps, no app calls; a mapped class key.
        recorder.Requests.Clear();
        var mapped = await Clients(recorder).DiscoverAsync(DiscoverySourceKind.Intune,
            new DiscoverySettings { IncludeApps = false, ClassMap = new Dictionary<string, string> { ["computer"] = "staff-pc" } }, connector, settings, "app-secret", null, log, Ct);
        Assert.Equal("staff-pc", mapped.Single(r => r.ExternalId == "dev-1").ClassKey);
        Assert.DoesNotContain(recorder.Requests, r => r.Url.Contains("detectedApps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Entra_devices_skip_disabled_ones_and_Azure_reads_resources_across_pages_by_type()
    {
        var recorder = new Recorder((request, body) => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/token", StringComparison.Ordinal) => Json(new { access_token = "token", expires_in = 3600 }),
            var p when p.EndsWith("/devices", StringComparison.Ordinal) => Json(new
            {
                value = new object[]
                {
                    new { id = "o-1", deviceId = "d-1", displayName = "PC-1", operatingSystem = "Windows", operatingSystemVersion = "11", accountEnabled = true },
                    new { id = "o-2", deviceId = "d-2", displayName = "PC-2", operatingSystem = "Windows", accountEnabled = false },
                },
            }),
            _ when body.Contains("$skipToken", StringComparison.Ordinal) => Json(new
            {
                data = new object[] { new { id = "/subscriptions/S/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/logs", name = "logs", type = "microsoft.storage/storageaccounts", location = "uaenorth" } },
            }),
            _ => Json(new
            {
                data = new object[]
                {
                    new { id = "/subscriptions/S/resourceGroups/RG/providers/Microsoft.Compute/virtualMachines/vm-1", name = "vm-1", type = "microsoft.compute/virtualmachines", location = "uaenorth",
                        computerName = "vm-1.entity.local", osType = "Linux" },
                    new { id = "/subscriptions/S/resourceGroups/rg/providers/Microsoft.Sql/servers/s/databases/payroll", name = "payroll", type = "microsoft.sql/servers/databases", location = "uaecentral" },
                },
            }),
        });
        var (connector, settings) = Entra();
        var log = new ExchangeLog();
        var devices = await Clients(recorder).DiscoverAsync(DiscoverySourceKind.EntraDevices, new DiscoverySettings(), connector, settings, "s", null, log, Ct);
        Assert.Equal("d-1", Assert.Single(devices).ExternalId);
        Assert.Equal("PC-1", (string)devices[0].Attributes["hostName"]!);

        var subscription = Guid.NewGuid().ToString();
        var azureRecorder = new Recorder((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                return Json(new { access_token = "arm-token", expires_in = 3600 });
            }

            return body.Contains("\"$skipToken\"", StringComparison.Ordinal)
                ? Json(new { data = new object[] { new { id = "/subscriptions/S/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/logs", name = "logs", type = "microsoft.storage/storageaccounts", location = "uaenorth" } } })
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"/subscriptions/S/resourceGroups/RG/providers/Microsoft.Compute/virtualMachines/vm-1","name":"vm-1","type":"microsoft.compute/virtualmachines","location":"uaenorth","computerName":"vm-1.entity.local","osType":"Linux"},{"id":"/subscriptions/S/resourceGroups/rg/providers/Microsoft.Sql/servers/s/databases/payroll","name":"payroll","type":"microsoft.sql/servers/databases","location":"uaecentral"}],"$skipToken":"next"}""",
                        Encoding.UTF8, "application/json"),
                };
        });
        var resources = await Clients(azureRecorder).DiscoverAsync(DiscoverySourceKind.Azure, new DiscoverySettings { SubscriptionIds = [subscription] }, connector, settings, "s", null, log, Ct);
        Assert.Equal(["virtual-machine", "cloud-database", "storage"], resources.Select(r => r.ClassKey));
        var vm = resources[0];
        Assert.Equal("/subscriptions/s/resourcegroups/rg/providers/microsoft.compute/virtualmachines/vm-1", vm.ExternalId);
        Assert.Equal("vm-1.entity.local", (string)vm.Attributes["hostName"]!);
        Assert.Equal("azure", (string)vm.Attributes["provider"]!);
        var query = azureRecorder.Requests.Where(r => r.Method == HttpMethod.Post && r.Url.Contains("ResourceGraph", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, query.Count);
        Assert.Contains(subscription, query[0].Body, StringComparison.Ordinal);
        Assert.Contains("microsoft.compute/virtualmachines", (string)System.Text.Json.Nodes.JsonNode.Parse(query[0].Body)!["query"]!, StringComparison.Ordinal);
        Assert.Contains("management.azure.com", azureRecorder.Requests.First(r => r.Url.Contains("/token", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VCenter_signs_in_reads_clusters_hosts_and_virtual_machines_with_their_hosts_and_keeps_the_session_out_of_the_log()
    {
        var session = Guid.NewGuid().ToString("N");
        var recorder = new Recorder((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            return (request.Method.Method, path) switch
            {
                ("POST", "/api/session") => Json(session, HttpStatusCode.Created),
                ("DELETE", "/api/session") => new HttpResponseMessage(HttpStatusCode.NoContent),
                (_, "/api/vcenter/cluster") => Json(new[] { new { cluster = "domain-c1", name = "Cluster A" } }),
                (_, "/api/vcenter/host") when query.Contains("clusters", StringComparison.Ordinal) => Json(new[] { new { host = "host-1", name = "esx-01.entity.local" } }),
                (_, "/api/vcenter/host") => Json(new[] { new { host = "host-1", name = "esx-01.entity.local" } }),
                (_, "/api/vcenter/vm") => Json(new object[] { new { vm = "vm-7", name = "app-01", cpu_count = 4, memory_size_MiB = 16384 }, new { vm = "vm-8", name = "off-01", cpu_count = 2, memory_size_MiB = 512 } }),
                (_, "/api/vcenter/vm/vm-7/guest/identity") => Json(new { host_name = "app-01.entity.local", full_name = new { default_message = "Ubuntu Linux (64-bit)" } }),
                _ => Json(new { error_type = "SERVICE_UNAVAILABLE" }, HttpStatusCode.ServiceUnavailable),
            };
        });
        var log = new ExchangeLog();
        var password = MessagingApi.Secret("vc");
        var records = await Clients(recorder).DiscoverAsync(DiscoverySourceKind.VCenter,
            new DiscoverySettings { Url = "https://vcenter.entity.local", Username = "zelvo@vsphere.local" }, null, null, null, password, log, Ct);

        var signIn = recorder.Requests[0];
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"zelvo@vsphere.local:{password}")), signIn.Authorization);
        Assert.All(recorder.Requests.Skip(1), r => Assert.Equal(session, r.Headers["vmware-api-session-id"]));
        Assert.DoesNotContain(log.Exchanges, e => e.ResponseBody?.Contains(session, StringComparison.Ordinal) == true);
        Assert.Equal("DELETE", recorder.Requests[^1].Method.Method);

        var cluster = records.Single(r => r.ExternalId == "cluster:domain-c1");
        Assert.Equal("virtualisation-cluster", cluster.ClassKey);
        Assert.Equal(new DiscoveredLink("depends-on", "host:host-1"), Assert.Single(cluster.Links!));
        Assert.Equal("server", records.Single(r => r.ExternalId == "host:host-1").ClassKey);
        var app = records.Single(r => r.ExternalId == "vm:vm-7");
        Assert.Equal(("app-01.entity.local", 4, 16m, "Ubuntu Linux (64-bit)"),
            ((string)app.Attributes["hostName"]!, (int)app.Attributes["cpuCores"]!, (decimal)app.Attributes["memoryGb"]!, (string)app.Attributes["operatingSystem"]!));
        Assert.Equal(new DiscoveredLink("runs-on", "host:host-1"), Assert.Single(app.Links!));
        // A VM without VMware Tools keeps its name as host name and has no memory below 1 GB.
        var off = records.Single(r => r.ExternalId == "vm:vm-8");
        Assert.Equal("off-01", (string)off.Attributes["hostName"]!);
        Assert.False(off.Attributes.ContainsKey("memoryGb"));
    }

    [Fact]
    public void Active_Directory_computers_and_settings_checks()
    {
        var guid = Guid.NewGuid();
        var server = new LdapEntry("CN=SRV-01,OU=Servers,DC=entity,DC=ae", new LdapAttributeSet
        {
            new LdapAttribute("objectGUID", guid.ToByteArray()), new LdapAttribute("name", "SRV-01"), new LdapAttribute("dNSHostName", "srv-01.entity.ae"),
            new LdapAttribute("operatingSystem", "Windows Server 2022 Datacenter"), new LdapAttribute("userAccountControl", "4096"),
        });
        var record = ConnectorClients.AdComputer(server, "objectGUID", new DiscoverySettings())!;
        Assert.Equal((guid.ToString(), "server", "srv-01.entity.ae"), (record.ExternalId, record.ClassKey, (string)record.Attributes["hostName"]!));
        var disabled = new LdapEntry("CN=PC-9,DC=entity,DC=ae", new LdapAttributeSet { new LdapAttribute("name", "PC-9"), new LdapAttribute("userAccountControl", "4098") });
        Assert.Null(ConnectorClients.AdComputer(disabled, "objectGUID", new DiscoverySettings()));
        var pc = new LdapEntry("CN=PC-1,DC=entity,DC=ae", new LdapAttributeSet { new LdapAttribute("name", "PC-1"), new LdapAttribute("operatingSystem", "Windows 11 Pro") });
        Assert.Equal(("computer", "PC-1"), (ConnectorClients.AdComputer(pc, "objectGUID", new DiscoverySettings())!.ClassKey, ConnectorClients.AdComputer(pc, "objectGUID", new DiscoverySettings())!.Name));

        Assert.Equal("AA:BB:CC:DD:EE:FF", ConnectorClients.NormalizeMac("aa-bb-cc-dd-ee-ff"));
        Assert.Null(ConnectorClients.NormalizeMac("00:00:00:00:00:00"));
        Assert.Null(ConnectorClients.NormalizeMac("not a mac"));

        Assert.Contains(("resourceTypes", "resource-type-invalid"), new DiscoverySettings { ResourceTypes = ["x') | evil"] }.Problems(DiscoverySourceKind.Azure));
        Assert.Contains(("subscriptionIds", "subscription-invalid"), new DiscoverySettings { SubscriptionIds = ["not-a-guid"] }.Problems(DiscoverySourceKind.Azure));
        Assert.Contains(("computerFilter", "filter-invalid"), new DiscoverySettings { ComputerFilter = "(objectCategory=computer" }.Problems(DiscoverySourceKind.ActiveDirectory));
        Assert.Contains(("url", "url-not-https"), new DiscoverySettings { Url = "http://vc", Username = "u" }.Problems(DiscoverySourceKind.VCenter));
        Assert.Contains(("certificateThumbprint", "thumbprint-invalid"),
            new DiscoverySettings { Url = "https://vc", Username = "u", CertificateThumbprint = "12:34" }.Problems(DiscoverySourceKind.VCenter));
        Assert.Empty(new DiscoverySettings { Url = "https://vc", Username = "u", CertificateThumbprint = string.Join(':', Enumerable.Repeat("AB", 32)) }.Problems(DiscoverySourceKind.VCenter));
    }
}

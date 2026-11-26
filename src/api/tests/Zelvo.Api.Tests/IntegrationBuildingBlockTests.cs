using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Zelvo.Api.Integrations;
using Zelvo.Api.Integrations.Bulk;
using Zelvo.Integrations;
using Zelvo.Licensing;

namespace Zelvo.Api.Tests;

/// <summary>Module 14 building blocks without a database: masking, outgoing addresses, signatures, files, keys, test installation.</summary>
public sealed class IntegrationBuildingBlockTests
{
    [Fact]
    public void Masking_hides_personal_data_by_property_name_and_by_value_and_keeps_the_rest_readable()
    {
        // A real-looking key made at run time, so no key-shaped value is ever committed.
        var key = ApiKeys.Create().Key;
        var body = $$"""
            {"id":"0195f0a2-1c3b-7d4e-8f90-123456789abc","email":"sara@example.ae","displayName":"Sara Al Mansoori",
             "status":"Active","roles":["agent"],"name":{"en":"Agent","ar":"موظف"},"count":12345,
             "note":"Call +971 50 123 4567 or mail ali@example.com, EID 784-1990-1234567-1, IBAN AE070331234567890123456",
             "createdAt":"2026-11-22T10:15:00Z","clientSecret":"abc","nested":[{"phoneNumber":"0501234567"}],
             "key":"{{key}}"}
            """;

        var masked = JsonNode.Parse(PersonalDataMasker.MaskBody(body, "application/json", 10_000)!)!;

        Assert.Equal("0195f0a2-1c3b-7d4e-8f90-123456789abc", (string?)masked["id"]);
        Assert.Equal(PersonalDataMasker.Mask, (string?)masked["email"]);
        Assert.Equal(PersonalDataMasker.Mask, (string?)masked["displayName"]);
        Assert.Equal(PersonalDataMasker.Mask, (string?)masked["clientSecret"]);
        Assert.Equal(PersonalDataMasker.Mask, (string?)masked["nested"]![0]!["phoneNumber"]);
        Assert.Equal("Active", (string?)masked["status"]);
        Assert.Equal("agent", (string?)masked["roles"]![0]);
        Assert.Equal("Agent", (string?)masked["name"]!["en"]);
        Assert.Equal(12345, (int?)masked["count"]);
        Assert.Equal("2026-11-22T10:15:00Z", (string?)masked["createdAt"]);
        var note = (string?)masked["note"];
        Assert.DoesNotContain("123 4567", note, StringComparison.Ordinal);
        Assert.DoesNotContain("ali@example.com", note, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567-1", note, StringComparison.Ordinal);
        Assert.DoesNotContain("AE0703", note, StringComparison.Ordinal);
        Assert.Equal(PersonalDataMasker.Mask, (string?)masked["key"]);
        Assert.DoesNotContain(key, masked.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Masking_handles_query_strings_text_and_long_bodies()
    {
        Assert.Equal("?page=2&email=[masked]&q=[masked]", Uri.UnescapeDataString(
            PersonalDataMasker.MaskQueryString("?page=2&email=a%40b.ae&q=sara%40example.ae")));
        Assert.Equal("Bearer [masked] from [masked]", PersonalDataMasker.MaskText("Bearer eyJhbGciOi.abc.def from x@y.ae"));
        var cut = PersonalDataMasker.MaskBody(new string('a', 100), "text/plain", 10)!;
        Assert.Equal(10, cut.Length);
        Assert.Null(PersonalDataMasker.MaskBody(null, "application/json", 10));
        Assert.True(PersonalDataMasker.IsSensitiveName("first_name"));
        Assert.True(PersonalDataMasker.IsSensitiveName("managerEmail"));
        Assert.False(PersonalDataMasker.IsSensitiveName("roleKey"));
    }

    public static TheoryData<string> BlockedAddresses =>
        ["127.0.0.1", "127.8.9.10", "0.0.0.0", "169.254.169.254", "169.254.1.1", "10.1.2.3", "172.20.0.5", "192.168.1.10", "100.64.0.1",
         "224.0.0.1", "255.255.255.255", "::1", "fe80::1", "fd12:3456::1", "fd00:ec2::254", "::ffff:127.0.0.1", "::ffff:10.0.0.1"];

    [Theory]
    [MemberData(nameof(BlockedAddresses))]
    public void Webhooks_cannot_reach_this_server_internal_or_reserved_addresses(string address)
    {
        var policy = new OutboundAddressPolicy(new WebhookOptions());
        Assert.False(policy.IsAllowed(IPAddress.Parse(address)));
    }

    [Fact]
    public void Operators_can_allow_private_networks_but_never_loopback_or_metadata()
    {
        var options = IntegrationOptions.From(Configuration(new()
        {
            ["Integrations:Webhooks:AllowedPrivateNetworks:0"] = "10.20.0.0/16",
            ["Integrations:Webhooks:AllowedPrivateNetworks:1"] = "127.0.0.0/8",
            ["Integrations:Webhooks:AllowedPrivateNetworks:2"] = "169.254.169.254",
        }));
        var policy = new OutboundAddressPolicy(options.Webhooks);

        Assert.True(policy.IsAllowed(IPAddress.Parse("10.20.4.5")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.21.4.5")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("127.0.0.1")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("169.254.169.254")));
        Assert.True(policy.IsAllowed(IPAddress.Parse("93.184.216.34")));
        Assert.True(policy.IsAllowed(IPAddress.Parse("2606:4700::1111")));
    }

    [Theory]
    [InlineData("https://hooks.example.ae/zelvo", null)]
    [InlineData("http://hooks.example.ae/zelvo", "url-not-https")]
    [InlineData("ftp://hooks.example.ae/zelvo", "url-not-https")]
    [InlineData("https://user:pass@hooks.example.ae/", "url-invalid")]
    [InlineData("https://hooks.example.ae/#frag", "url-invalid")]
    [InlineData("https://127.0.0.1/hook", "address-not-allowed")]
    [InlineData("https://[::1]/hook", "address-not-allowed")]
    [InlineData("https://169.254.169.254/latest/meta-data", "address-not-allowed")]
    [InlineData("https://localhost/hook", "address-not-allowed")]
    [InlineData("https://api.localhost/hook", "address-not-allowed")]
    [InlineData("not a url", "url-invalid")]
    public void Receiver_urls_are_checked_when_saved(string url, string? expected)
    {
        var policy = new OutboundAddressPolicy(new WebhookOptions());
        Assert.Equal(expected, policy.CheckUrl(url, out _));
    }

    [Fact]
    public async Task Names_that_resolve_to_internal_addresses_are_refused_when_connecting()
    {
        var policy = new OutboundAddressPolicy(new WebhookOptions());
        var refused = await Assert.ThrowsAsync<OutboundRequestBlockedException>(async () =>
            await policy.ConnectAsync("localhost", 443, TestContext.Current.CancellationToken));
        Assert.Equal("address-not-allowed", refused.Code);
    }

    [Fact]
    public void Signatures_follow_standard_webhooks_and_carry_every_secret_during_rotation()
    {
        var current = WebhookSigner.NewSecret();
        var previous = WebhookSigner.NewSecret();
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_763_800_000);
        const string body = """{"type":"user.created"}""";

        var header = WebhookSigner.Sign("msg_1", timestamp, body, [current, previous]);

        Assert.StartsWith(WebhookSigner.SecretPrefix, current, StringComparison.Ordinal);
        Assert.Equal(2, header.Split(' ').Length);
        Assert.All(header.Split(' '), s => Assert.StartsWith("v1,", s, StringComparison.Ordinal));
        Assert.True(WebhookSigner.Verify("msg_1", timestamp, body, header, current));
        Assert.True(WebhookSigner.Verify("msg_1", timestamp, body, header, previous));
        Assert.False(WebhookSigner.Verify("msg_1", timestamp, body + " ", header, current));
        Assert.False(WebhookSigner.Verify("msg_2", timestamp, body, header, current));
        Assert.False(WebhookSigner.Verify("msg_1", timestamp.AddSeconds(1), body, header, current));

        // Known value: HMAC-SHA256 keyed with the decoded secret over "id.timestamp.body".
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        var expected = "v1," + Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"), Encoding.UTF8.GetBytes($"msg_1.1763800000.{body}")));
        Assert.Equal(expected, WebhookSigner.Sign("msg_1", timestamp, body, [WebhookSigner.SecretPrefix + key]));
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    public void Files_round_trip_arabic_quotes_and_line_breaks_and_neutralise_formulas(TabularFormat format)
    {
        string[] header = ["email", "displayName", "note"];
        IReadOnlyList<string?>[] rows =
        [
            ["sara@example.ae", "سارة المنصوري", "has, comma"],
            ["ali@example.ae", "Ali \"The\" Agent", "two\r\nlines"],
            ["x@example.ae", "=HYPERLINK(\"http://evil\")", "+1"],
            ["y@example.ae", null, "@SUM(A1)"],
        ];

        var bytes = TabularFile.Write(format, header, rows);
        using var stream = new MemoryStream(bytes);
        var read = TabularFile.Read(format, stream, 100);

        Assert.Equal(header, read[0]);
        Assert.Equal("سارة المنصوري", read[1][1]);
        Assert.Equal("has, comma", read[1][2]);
        Assert.Equal("Ali \"The\" Agent", read[2][1]);
        Assert.Equal("two\nlines", read[2][2].Replace("\r", "", StringComparison.Ordinal));
        Assert.Equal("'=HYPERLINK(\"http://evil\")", read[3][1]);
        Assert.Equal("'+1", read[3][2]);
        Assert.Equal("'@SUM(A1)", read[4][2]);
    }

    [Fact]
    public void Csv_exports_start_with_a_byte_order_mark_so_excel_reads_arabic()
    {
        var bytes = TabularFile.Write(TabularFormat.Csv, ["name"], [["زيلفو"]]);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
    }

    [Fact]
    public void Files_with_too_many_rows_or_broken_content_are_refused()
    {
        var many = TabularFile.Write(TabularFormat.Csv, ["email"], Enumerable.Range(0, 11).Select(i => (IReadOnlyList<string?>)[$"u{i}@example.ae"]));
        using var manyStream = new MemoryStream(many);
        Assert.Throws<TabularFileException>(() => TabularFile.Read(TabularFormat.Csv, manyStream, 10));

        using var broken = new MemoryStream(Encoding.UTF8.GetBytes("email\n\"open"));
        Assert.Throws<TabularFileException>(() => TabularFile.Read(TabularFormat.Csv, broken, 10));

        using var notExcel = new MemoryStream(Encoding.UTF8.GetBytes("not a zip"));
        Assert.Throws<TabularFileException>(() => TabularFile.Read(TabularFormat.Xlsx, notExcel, 10));
    }

    [Fact]
    public void Api_keys_are_random_hashed_and_recognised_by_their_prefix()
    {
        var (prefix, hash, key) = ApiKeys.Create();
        var (otherPrefix, _, otherKey) = ApiKeys.Create();

        Assert.StartsWith($"zvk_{prefix}_", key, StringComparison.Ordinal);
        Assert.NotEqual(key, otherKey);
        Assert.NotEqual(prefix, otherPrefix);
        Assert.Equal(prefix, ApiKeys.PrefixOf(key));
        Assert.Equal(hash, ApiKeys.Hash(key));
        Assert.Null(ApiKeys.PrefixOf("zvk_short"));
        Assert.Null(ApiKeys.PrefixOf("Bearer something"));
        Assert.Null(ApiKeys.PrefixOf($"zvk_{prefix.ToUpperInvariant()}_x"));
    }

    [Theory]
    [InlineData("Test", LicenseKind.Test, "test")]
    [InlineData("test", LicenseKind.Test, "test")]
    [InlineData("Demo", LicenseKind.Demo, "demo")]
    [InlineData(null, LicenseKind.Development, "development")]
    public async Task The_test_installation_runs_under_a_test_license(string? profile, LicenseKind kind, string environment)
    {
        var service = LicensingTestSupport.Development("Development", Configuration(new() { [InstallationProfile.ConfigurationKey] = profile }));

        var status = await service.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsValid);
        Assert.Equal(kind, status.Kind);
        Assert.Equal(kind == LicenseKind.Test, service.IsTestInstallation());
        Assert.Equal(environment, service.EnvironmentName());
        Assert.All(LicensedModules.Phase1, module => Assert.True(service.IsModuleLicensed(module)));
    }

    [Fact]
    public async Task Api_responses_and_system_info_say_which_installation_answered()
    {
        await using var factory = new ApiFactory().WithSetting(InstallationProfile.ConfigurationKey, InstallationProfile.Test);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/system/info", UriKind.Relative), TestContext.Current.CancellationToken);
        var info = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal("test", response.Headers.GetValues("Zelvo-Environment").Single());
        Assert.Equal("test", (string?)info["environment"]);
        Assert.Equal("Test", (string?)info["licenseMode"]);
    }

    [Fact]
    public async Task The_api_description_offers_api_keys_and_bearer_tokens_for_the_explorer()
    {
        await using var factory = new ApiFactory { EnvironmentName = Environments.Development };
        using var client = factory.CreateClient();

        var document = JsonNode.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken))!;

        var schemes = document["components"]!["securitySchemes"]!;
        Assert.Equal("apiKey", (string?)schemes["apiKey"]!["type"]);
        Assert.Equal("X-Api-Key", (string?)schemes["apiKey"]!["name"]);
        Assert.Equal("bearer", (string?)schemes["bearer"]!["scheme"]);
        Assert.NotNull(document["paths"]!["/api/v1/integrations/clients"]);
        Assert.NotNull(document["paths"]!["/api/v1/bulk/{type}/import"]);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}

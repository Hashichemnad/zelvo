using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using Zelvo.Licensing;
using Zelvo.SigningTool;

namespace Zelvo.Api.Tests;

/// <summary>The offline signing tool (module 12, task F3), with a key it makes at run time in a temporary folder.</summary>
public sealed class SigningToolTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"zelvo-sign-{Guid.NewGuid():N}");
    private readonly string _passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly LicensingTestSupport _support = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _support.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_tool_makes_a_key_and_issues_a_license_the_installation_accepts()
    {
        var (code, output, _) = await RunAsync("keygen", "--out", _folder, "--id", "zelvo-test");
        Assert.Equal(0, code);
        var privateKey = Path.Combine(_folder, "zelvo-test.private.pem");
        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", await File.ReadAllTextAsync(privateKey, Ct), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(privateKey));
        }

        Assert.Contains("vendor-keys.json", output, StringComparison.Ordinal);
        var publicKey = System.Text.Json.JsonSerializer.Deserialize<VendorKey>(await File.ReadAllTextAsync(Path.Combine(_folder, "zelvo-test.public.json"), Ct))!;

        // The installation trusts the new public key and makes an activation request.
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(_support.Settings())
        {
            ["Licensing:DevelopmentKeys:0:Id"] = publicKey.Id, ["Licensing:DevelopmentKeys:0:PublicKey"] = publicKey.PublicKey,
        }).Build();
        var service = new LicenseService(_support.Store, new ServerFingerprintSource(configuration), VendorKeys.Load(configuration),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" }, configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LicenseService>.Instance);
        var request = await service.CreateActivationRequestAsync(Ct);
        var requestPath = Path.Combine(_folder, request.FileName);
        await File.WriteAllBytesAsync(requestPath, request.Content.ToArray(), Ct);

        (code, output, _) = await RunAsync("inspect", requestPath);
        Assert.Equal(0, code);
        Assert.Contains(_support.Store.InstallationId, output, StringComparison.Ordinal);

        var licensePath = Path.Combine(_folder, "license.json");
        (code, output, _) = await RunAsync("issue", "--request", requestPath, "--key", privateKey, "--license-id", "LIC-2026-0001", "--customer-id", "C-0042",
            "--customer-en", "Example Authority", "--customer-ar", "هيئة المثال", "--modules", "m01-incidents,m08-sla", "--version", "1.0", "--out", licensePath);
        Assert.Equal(0, code);
        Assert.Matches("Watermark: ZW-", output);

        var result = await service.ImportLicenseAsync(File.OpenRead(licensePath), new LicenseImportContext(null, null, "license.json"), Ct);
        Assert.True(result.Accepted, result.Reason);
        Assert.Equal((true, false), (service.IsModuleLicensed(LicensedModules.Incidents), service.IsModuleLicensed(LicensedModules.Payments)));

        (code, _, _) = await RunAsync("verify", licensePath, "--keys", WriteKeys(publicKey));
        Assert.Equal(0, code);

        // An upgrade for the installed license, from a new activation request of the same servers.
        var upgradeRequest = Path.Combine(_folder, "upgrade-request.json");
        await File.WriteAllBytesAsync(upgradeRequest, (await service.CreateActivationRequestAsync(Ct)).Content.ToArray(), Ct);
        var upgradePath = Path.Combine(_folder, "upgrade.json");
        (code, _, _) = await RunAsync("upgrade", "--request", upgradeRequest, "--key", privateKey, "--version", "1.1", "--out", upgradePath);
        Assert.Equal(0, code);
        Assert.True((await service.ImportLicenseAsync(File.OpenRead(upgradePath), new LicenseImportContext(null, null, null), Ct)).Accepted);
        Assert.True(service.IsVersionLicensed(new Version(1, 1, 3)));
    }

    [Fact]
    public async Task The_tool_refuses_to_write_a_key_inside_a_git_working_tree_or_open_it_with_a_wrong_passphrase()
    {
        var repository = Path.Combine(_folder, "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var (code, _, error) = await RunAsync("keygen", "--out", Path.Combine(repository, "keys"));
        Assert.Equal(2, code);
        Assert.Contains("git working tree", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(repository, "keys")));

        Assert.Equal(0, (await RunAsync("keygen", "--out", _folder, "--id", "zelvo-other")).Code);
        var request = Path.Combine(_folder, "request.json");
        await File.WriteAllBytesAsync(request, (await _support.Service().CreateActivationRequestAsync(Ct)).Content.ToArray(), Ct);
        var wrong = await SigningTool.SigningTool.RunAsync(["issue", "--request", request, "--key", Path.Combine(_folder, "zelvo-other.private.pem"), "--license-id", "L",
            "--customer-id", "C", "--customer-en", "X", "--out", Path.Combine(_folder, "l.json")], TextWriter.Null, TextWriter.Null, _ => "not the passphrase at all");
        Assert.Equal(2, wrong);

        // A demo license cannot be issued for a standard installation's request.
        var (demo, _, demoError) = await RunAsync("issue", "--request", request, "--key", Path.Combine(_folder, "zelvo-other.private.pem"), "--license-id", "L",
            "--customer-id", "C", "--customer-en", "X", "--kind", "demo", "--out", Path.Combine(_folder, "l.json"));
        Assert.Equal(2, demo);
        Assert.Contains("Standard installation", demoError, StringComparison.Ordinal);
    }

    private string WriteKeys(VendorKey key)
    {
        var path = Path.Combine(_folder, "keys.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { key }));
        return path;
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await SigningTool.SigningTool.RunAsync(args, output, error, _ => _passphrase);
        return (code, output.ToString(), error.ToString());
    }
}

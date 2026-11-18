using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Zelvo.Secrets;

namespace Zelvo.Api.Tests;

/// <summary>
/// The Vault Transit key provider against a real OpenBao (same API as HashiCorp Vault) in
/// development mode: token and AppRole sign-in, wrapping, and rewrapping after the Transit key is
/// rotated in Vault.
/// </summary>
public sealed class VaultTransitTests : IAsyncLifetime
{
    private const string RootToken = "test-only-root-token";
    private readonly string _files = Directory.CreateTempSubdirectory("zelvo-vault-").FullName;

    private readonly IContainer _openBao = new ContainerBuilder(
            Environment.GetEnvironmentVariable("ZELVO_TEST_OPENBAO_IMAGE") ?? "openbao/openbao:2.7.2")
        .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200", $"-dev-root-token-id={RootToken}")
        .WithPortBinding(8200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
        .Build();

    private HttpClient _admin = null!;

    private Uri Address => new($"http://{_openBao.Hostname}:{_openBao.GetMappedPublicPort(8200)}/");

    public async ValueTask InitializeAsync()
    {
        await _openBao.StartAsync(TestContext.Current.CancellationToken);
        _admin = new HttpClient { BaseAddress = Address };
        _admin.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);

        await AdminAsync(HttpMethod.Post, "v1/sys/mounts/transit", new JsonObject { ["type"] = "transit" });
        await AdminAsync(HttpMethod.Post, "v1/transit/keys/zelvo", new JsonObject { ["type"] = "aes256-gcm96" });

        // Least privilege for Zelvo: encrypt, decrypt and rewrap with its key only.
        await AdminAsync(HttpMethod.Put, "v1/sys/policies/acl/zelvo", new JsonObject
        {
            ["policy"] = """
                path "transit/encrypt/zelvo" { capabilities = ["update"] }
                path "transit/decrypt/zelvo" { capabilities = ["update"] }
                path "transit/rewrap/zelvo" { capabilities = ["update"] }
                """,
        });
        await AdminAsync(HttpMethod.Post, "v1/sys/auth/approle", new JsonObject { ["type"] = "approle" });
        await AdminAsync(HttpMethod.Post, "v1/auth/approle/role/zelvo", new JsonObject { ["token_policies"] = new JsonArray("zelvo"), ["token_ttl"] = "1h" });
    }

    public async ValueTask DisposeAsync()
    {
        _admin?.Dispose();
        await _openBao.DisposeAsync();
        Directory.Delete(_files, recursive: true);
    }

    [Fact]
    public async Task A_token_signs_in_and_data_keys_are_wrapped_by_the_transit_key()
    {
        using var key = new VaultTransitKeyEncryptionKey(new VaultKeyOptions { Address = Address, TokenFile = Write("token", RootToken) });
        var dataKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = await key.WrapAsync(dataKey, TestContext.Current.CancellationToken);

        Assert.Equal("vault:transit/zelvo", key.Id);
        Assert.StartsWith("vault:v1:", System.Text.Encoding.ASCII.GetString(wrapped), StringComparison.Ordinal);
        Assert.Equal(dataKey, await key.UnwrapAsync(key.Id, wrapped, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppRole_signs_in_with_only_the_rights_zelvo_needs_and_rewraps_after_rotation()
    {
        var roleId = (string)(await AdminAsync(HttpMethod.Get, "v1/auth/approle/role/zelvo/role-id"))!["data"]!["role_id"]!;
        var secretId = (string)(await AdminAsync(HttpMethod.Post, "v1/auth/approle/role/zelvo/secret-id", new JsonObject()))!["data"]!["secret_id"]!;
        using var key = new VaultTransitKeyEncryptionKey(new VaultKeyOptions
        {
            Address = Address,
            RoleIdFile = Write("role-id", roleId),
            SecretIdFile = Write("secret-id", secretId),
        });
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var wrapped = await key.WrapAsync(dataKey, TestContext.Current.CancellationToken);

        await AdminAsync(HttpMethod.Post, "v1/transit/keys/zelvo/rotate", new JsonObject());
        var rewrapped = await key.RewrapAsync(wrapped, TestContext.Current.CancellationToken);

        Assert.StartsWith("vault:v2:", System.Text.Encoding.ASCII.GetString(rewrapped), StringComparison.Ordinal);
        Assert.Equal(dataKey, await key.UnwrapAsync(key.Id, rewrapped, TestContext.Current.CancellationToken));
        Assert.Equal(dataKey, await key.UnwrapAsync(key.Id, wrapped, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refused_token_is_reported_without_key_material()
    {
        using var key = new VaultTransitKeyEncryptionKey(new VaultKeyOptions { Address = Address, TokenFile = Write("bad-token", "not-a-token") });

        var error = await Assert.ThrowsAsync<KeyEncryptionKeyException>(() => key.WrapAsync(new byte[32], TestContext.Current.CancellationToken));

        Assert.Contains("403", error.Message, StringComparison.Ordinal);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_files, name);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<JsonNode?> AdminAsync(HttpMethod method, string path, JsonObject? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await _admin.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }
}

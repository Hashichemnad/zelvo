using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Secrets;

namespace Zelvo.Api.Tests;

/// <summary>Encryption at rest for stored secrets (A5, non-functional requirement "Encryption").</summary>
public sealed class SecretsTests(DatabaseFixture database) : IDisposable
{
    private readonly string _keys = Directory.CreateTempSubdirectory("zelvo-keys-").FullName;

    public void Dispose() => Directory.Delete(_keys, recursive: true);

    [Fact]
    public async Task A_master_key_file_wraps_data_keys_that_only_it_unwraps()
    {
        var key = FileKeyEncryptionKey.Load(NewKeyFile());
        var other = FileKeyEncryptionKey.Load(NewKeyFile());
        var dataKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = await key.WrapAsync(dataKey, TestContext.Current.CancellationToken);

        Assert.Matches("^file:[0-9a-f]{16}$", key.Id);
        Assert.NotEqual(key.Id, other.Id);
        Assert.Equal(dataKey, await key.UnwrapAsync(key.Id, wrapped, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Convert.ToHexString(dataKey), Convert.ToHexString(wrapped), StringComparison.Ordinal);
        await Assert.ThrowsAsync<KeyEncryptionKeyException>(() => other.UnwrapAsync(other.Id, wrapped, TestContext.Current.CancellationToken));
        wrapped[^1] ^= 1;
        await Assert.ThrowsAsync<KeyEncryptionKeyException>(() => key.UnwrapAsync(key.Id, wrapped, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_master_key_file_must_hold_32_bytes_of_base64()
    {
        var shortKey = Path.Combine(_keys, "short.key");
        File.WriteAllText(shortKey, Convert.ToBase64String(new byte[16]));
        var notBase64 = Path.Combine(_keys, "text.key");
        File.WriteAllText(notBase64, "not a key");

        Assert.Throws<KeyEncryptionKeyException>(() => FileKeyEncryptionKey.Load(shortKey));
        Assert.Throws<KeyEncryptionKeyException>(() => FileKeyEncryptionKey.Load(notBase64));
        Assert.Throws<KeyEncryptionKeyException>(() => FileKeyEncryptionKey.Load(Path.Combine(_keys, "missing.key")));
        Assert.Throws<KeyEncryptionKeyException>(() => KeyRing.FromOptions(new SecretsOptions()));
    }

    [Fact]
    public async Task Secrets_are_stored_encrypted_with_the_tenants_data_key_and_decrypt_again()
    {
        var tenant = await database.CreateTenantAsync("sec");
        await using var services = Services(NewKeyFile());

        string stored;
        await using (var scope = ScopeFor(services, tenant))
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            stored = await protector.ProtectAsync("ai.provider.apiKey", "example-secret-value", TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<ZelvoDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.StartsWith(SecretProtector.Prefix, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("example-secret", stored, StringComparison.Ordinal);
            Assert.NotEqual(stored, await protector.ProtectAsync("ai.provider.apiKey", "example-secret-value", TestContext.Current.CancellationToken));
        }

        // A new scope (and a cold cache in a new process would be the same) decrypts it.
        await using (var scope = ScopeFor(services, tenant))
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            Assert.Equal("example-secret-value", await protector.UnprotectAsync("ai.provider.apiKey", stored, TestContext.Current.CancellationToken));

            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var key = await db.TenantDataKeys.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Null(key.RetiredAt);
            Assert.StartsWith("file:", key.KekId, StringComparison.Ordinal);
            Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Action == "secrets.dataKeyCreated" && a.EntityId == key.Id.ToString(), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_secret_does_not_decrypt_for_another_tenant_another_purpose_or_after_a_change()
    {
        var first = await database.CreateTenantAsync("sec");
        var second = await database.CreateTenantAsync("sec");
        await using var services = Services(NewKeyFile());

        string stored;
        await using (var scope = ScopeFor(services, first))
        {
            stored = await scope.ServiceProvider.GetRequiredService<ISecretProtector>().ProtectAsync("payments.gateway:1.secret", "gateway-secret", TestContext.Current.CancellationToken);
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            await Assert.ThrowsAsync<SecretDecryptionException>(() => protector.UnprotectAsync("payments.gateway:2.secret", stored, TestContext.Current.CancellationToken));
            var changed = stored[..^2] + (stored[^2] == 'A' ? "B" : "A") + stored[^1];
            await Assert.ThrowsAsync<SecretDecryptionException>(() => protector.UnprotectAsync("payments.gateway:1.secret", changed, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SecretDecryptionException>(() => protector.UnprotectAsync("payments.gateway:1.secret", "gateway-secret", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => protector.ProtectAsync("Not A Purpose", "x", TestContext.Current.CancellationToken));
        }

        await using (var scope = ScopeFor(services, second))
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            await Assert.ThrowsAsync<SecretDecryptionException>(() => protector.UnprotectAsync("payments.gateway:1.secret", stored, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Data_keys_are_isolated_per_tenant_and_cannot_be_deleted()
    {
        var first = await database.CreateTenantAsync("sec");
        var second = await database.CreateTenantAsync("sec");
        await using var services = Services(NewKeyFile());
        await using (var scope = ScopeFor(services, first))
        {
            await scope.ServiceProvider.GetRequiredService<ISecretProtector>().ProtectAsync("test.value", "x", TestContext.Current.CancellationToken);
        }

        await using (var other = await database.OpenAppConnectionAsync(second.Id))
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM tenant_data_keys", other);
            Assert.Equal(0L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await using var own = await database.OpenAppConnectionAsync(first.Id);
        await using var delete = new NpgsqlCommand("DELETE FROM tenant_data_keys", own);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task Rewrapping_moves_data_keys_to_a_new_master_key_and_secrets_still_decrypt()
    {
        var tenant = await database.CreateTenantAsync("sec");
        var oldKey = NewKeyFile();
        var newKey = NewKeyFile();
        string stored;
        await using (var services = Services(oldKey))
        await using (var scope = ScopeFor(services, tenant))
        {
            stored = await scope.ServiceProvider.GetRequiredService<ISecretProtector>().ProtectAsync("test.value", "before rotation", TestContext.Current.CancellationToken);
        }

        // New master key file; the old one stays configured until the rewrap is done.
        await using (var services = Services(newKey, oldKey))
        await using (var scope = ScopeFor(services, tenant))
        {
            var result = await scope.ServiceProvider.GetRequiredService<DataKeyRewrap>().RewrapAsync(retireActive: true, TestContext.Current.CancellationToken);
            Assert.Equal(new RewrapResult(1, 0), result);
        }

        // Without the old key: the rewrapped data key still decrypts, and new secrets get a new data key.
        await using (var services = Services(newKey))
        await using (var scope = ScopeFor(services, tenant))
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            Assert.Equal("before rotation", await protector.UnprotectAsync("test.value", stored, TestContext.Current.CancellationToken));
            var after = await protector.ProtectAsync("test.value", "after rotation", TestContext.Current.CancellationToken);
            Assert.Equal("after rotation", await protector.UnprotectAsync("test.value", after, TestContext.Current.CancellationToken));

            var keys = await scope.ServiceProvider.GetRequiredService<ZelvoDbContext>().TenantDataKeys.ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, keys.Count);
            Assert.All(keys, k => Assert.Equal(FileKeyEncryptionKey.Load(newKey).Id, k.KekId));
            Assert.Single(keys, k => k.RetiredAt is null);
        }
    }

    private string NewKeyFile()
    {
        var path = Path.Combine(_keys, $"{Guid.NewGuid():N}.key");
        File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return path;
    }

    private ServiceProvider Services(string keyFile, params string[] previous)
    {
        var settings = new Dictionary<string, string?> { ["Secrets:File:Path"] = keyFile };
        for (var i = 0; i < previous.Length; i++)
        {
            settings[$"Secrets:File:PreviousPaths:{i}"] = previous[i];
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZelvoData(database.AppConnectionString);
        services.AddZelvoSecrets(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static AsyncServiceScope ScopeFor(ServiceProvider services, Tenant tenant)
    {
        var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id, tenant.Slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "test", "test");
        return scope;
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Data;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>Attachment content in S3-compatible object storage (task F6), against PostgreSQL.</summary>
public sealed class ObjectAttachmentStoreTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly MemoryObjectStorage _objects = new();
    private ServiceProvider? _services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    [Fact]
    public void Storage_is_the_database_unless_s3_is_chosen_with_a_bucket()
    {
        Assert.False(AttachmentStorageOptions.From(new ConfigurationBuilder().Build()).UsesObjectStorage);
        Assert.Throws<InvalidOperationException>(() => AttachmentStorageOptions.From(Configuration(("Attachments:Storage", "S3"))));
        Assert.Throws<InvalidOperationException>(() => AttachmentStorageOptions.From(Configuration(("Attachments:Storage", "Disk"))));
        var s3 = AttachmentStorageOptions.From(Configuration(("Attachments:Storage", "S3"), ("Attachments:S3:Bucket", "zelvo"), ("Attachments:S3:Prefix", "files")));
        Assert.True(s3.UsesObjectStorage);
        var tenant = Guid.NewGuid();
        var file = Guid.NewGuid();
        Assert.Equal($"files/{tenant:D}/{file:D}", s3.Key(tenant, file));
    }

    [Fact]
    public async Task Files_go_to_object_storage_when_saved_and_only_their_tenant_reads_them()
    {
        var services = Services();
        var tenant = await database.CreateTenantAsync("s3");
        var other = await database.CreateTenantAsync("s3o");
        var content = RandomNumberGenerator.GetBytes(4096);

        Guid id;
        await using (var scope = Scope(services, tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();
            Assert.IsType<ObjectAttachmentStore>(store);
            var attachment = FormUpload(content);
            id = attachment.Id;
            db.TicketAttachments.Add(attachment);
            store.Add(id, content);
            Assert.Empty(_objects.Items);
            await db.SaveChangesAsync(Ct);
        }

        var key = Assert.Single(_objects.Items.Keys);
        Assert.Equal($"attachments/{tenant.Id:D}/{id:D}", key);
        await using (var scope = Scope(services, tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            Assert.Empty(await db.TicketAttachmentContents.Where(c => c.AttachmentId == id).Select(c => c.Content).SingleAsync(Ct));
            Assert.Equal(content, await scope.ServiceProvider.GetRequiredService<IAttachmentStore>().ReadAsync(id, Ct));
        }

        await using (var scope = Scope(services, other))
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IAttachmentStore>().ReadAsync(id, Ct));
        }
    }

    [Fact]
    public async Task Branding_images_and_files_stored_before_the_switch_are_read_from_the_database()
    {
        var services = Services();
        var tenant = await database.CreateTenantAsync("s3b");
        var logo = RandomNumberGenerator.GetBytes(512);
        var earlier = RandomNumberGenerator.GetBytes(256);
        Guid logoId, earlierId;
        await using (var scope = Scope(services, tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var image = new TicketAttachment
            {
                BrandingSlot = Zelvo.Data.Branding.BrandingSlot.LogoLight, FileName = "logo.png", ContentType = "image/png",
                Size = logo.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(logo)),
            };
            logoId = image.Id;
            db.TicketAttachments.Add(image);
            scope.ServiceProvider.GetRequiredService<IAttachmentStore>().Add(logoId, logo);
            var upload = FormUpload(earlier);
            earlierId = upload.Id;
            db.TicketAttachments.Add(upload);
            // As the database store wrote it before Attachments:Storage became S3.
            db.TicketAttachmentContents.Add(new TicketAttachmentContent { AttachmentId = earlierId, Content = earlier });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Empty(_objects.Items);
        await using (var scope = Scope(services, tenant))
        {
            var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();
            Assert.Equal(logo, await store.ReadAsync(logoId, Ct));
            Assert.Equal(earlier, await store.ReadAsync(earlierId, Ct));
        }
    }

    [Fact]
    public async Task The_sweeper_removes_objects_whose_file_is_gone()
    {
        var services = Services();
        var tenant = await database.CreateTenantAsync("s3s");
        var kept = FormUpload(RandomNumberGenerator.GetBytes(128));
        var removed = FormUpload(RandomNumberGenerator.GetBytes(128));
        await using (var scope = Scope(services, tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();
            db.TicketAttachments.AddRange(kept, removed);
            store.Add(kept.Id, [1, 2, 3]);
            store.Add(removed.Id, [4, 5, 6]);
            await db.SaveChangesAsync(Ct);
            await store.RemoveAsync(removed.Id, Ct);
            await db.SaveChangesAsync(Ct);
        }

        // An object a failed save left behind, with no row at all.
        var stray = $"attachments/{tenant.Id:D}/{Guid.NewGuid():D}";
        await _objects.PutAsync(stray, [7], Ct);
        _objects.AgeAll(TimeSpan.FromDays(2));

        var sweeper = new ObjectStorageSweeper(services.GetRequiredService<IServiceScopeFactory>(), _objects,
            services.GetRequiredService<AttachmentStorageOptions>(), TimeProvider.System, NullLogger<ObjectStorageSweeper>.Instance);
        Assert.True(await sweeper.SweepAsync(Ct) >= 2);

        Assert.Contains($"attachments/{tenant.Id:D}/{kept.Id:D}", _objects.Items.Keys);
        Assert.DoesNotContain($"attachments/{tenant.Id:D}/{removed.Id:D}", _objects.Items.Keys);
        Assert.DoesNotContain(stray, _objects.Items.Keys);
    }

    private static TicketAttachment FormUpload(byte[] content) => new()
    {
        FormFieldKey = "document", FileName = "document.pdf", ContentType = "application/pdf",
        Size = content.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
    };

    private ServiceProvider Services()
    {
        var configuration = Configuration(("Attachments:Storage", "S3"), ("Attachments:S3:Bucket", "zelvo-test"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZelvoData(database.AppConnectionString);
        services.AddScoped<ISearchIndexer, SearchIndexer>();
        services.AddSingleton<IObjectStorage>(_objects);
        services.AddZelvoTickets(configuration);
        _services = services.BuildServiceProvider();
        return _services;
    }

    private static AsyncServiceScope Scope(ServiceProvider services, Zelvo.Data.Tenancy.Tenant tenant)
    {
        var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id, tenant.Slug);
        return scope;
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private sealed class MemoryObjectStorage : IObjectStorage
    {
        public ConcurrentDictionary<string, (byte[] Content, DateTimeOffset At)> Items { get; } = new(StringComparer.Ordinal);

        public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken)
        {
            Items[key] = (content, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(key, out var item) ? item.Content : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Items.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<(string Key, DateTimeOffset LastModified)> ListAsync(
            string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var (key, item) in Items.Where(i => i.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                yield return (key, item.At);
            }
        }

        public void AgeAll(TimeSpan age)
        {
            foreach (var key in Items.Keys)
            {
                Items[key] = Items[key] with { At = Items[key].At - age };
            }
        }
    }
}

using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Tenancy;

namespace Zelvo.Tickets;

/// <summary>
/// Where attachment content is kept (configuration section <c>Attachments</c>; product owner
/// decision 2026-11-22): <c>Database</c> (default; PostgreSQL, under row-level security and in
/// the database backups) or <c>S3</c>, S3-compatible object storage for medium and large installs
/// (task F6).
/// </summary>
public sealed class AttachmentStorageOptions
{
    public string Storage { get; set; } = "Database";

    public string? Endpoint { get; set; }

    public string? Region { get; set; }

    public string? Bucket { get; set; }

    /// <summary>Path-style addresses (bucket in the path), which most S3-compatible servers need.</summary>
    public bool ForcePathStyle { get; set; } = true;

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Keys start with this, then the tenant id and the attachment id.</summary>
    public string Prefix { get; set; } = "attachments/";

    /// <summary>Objects without an attachment row are removed after this long (failed saves, deleted files).</summary>
    public TimeSpan OrphanAge { get; set; } = TimeSpan.FromDays(1);

    public bool UsesObjectStorage => string.Equals(Storage, "S3", StringComparison.OrdinalIgnoreCase);

    public static AttachmentStorageOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Attachments");
        var s3 = section.GetSection("S3");
        var options = new AttachmentStorageOptions
        {
            Storage = section["Storage"] is { Length: > 0 } storage ? storage : "Database",
            Endpoint = s3["Endpoint"],
            Region = s3["Region"],
            Bucket = s3["Bucket"],
            AccessKey = s3["AccessKey"],
            SecretKey = s3["SecretKey"],
        };
        if (bool.TryParse(s3["ForcePathStyle"], out var pathStyle))
        {
            options.ForcePathStyle = pathStyle;
        }

        if (s3["Prefix"] is { Length: > 0 } prefix)
        {
            options.Prefix = prefix.EndsWith('/') ? prefix : prefix + "/";
        }

        if (options.UsesObjectStorage && string.IsNullOrWhiteSpace(options.Bucket))
        {
            throw new InvalidOperationException("Attachments:S3:Bucket is required when Attachments:Storage is S3.");
        }

        if (!options.UsesObjectStorage && !string.Equals(options.Storage, "Database", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Attachments:Storage is Database or S3.");
        }

        return options;
    }

    /// <summary>The object key of an attachment of a tenant.</summary>
    public string Key(Guid tenantId, Guid attachmentId) => $"{Prefix}{tenantId:D}/{attachmentId:D}";
}

/// <summary>An object store: S3 or, in tests, memory.</summary>
public interface IObjectStorage
{
    Task PutAsync(string key, byte[] content, CancellationToken cancellationToken);

    /// <summary>The object's bytes, or null when there is none.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>The objects under <paramref name="prefix"/> with their last change.</summary>
    IAsyncEnumerable<(string Key, DateTimeOffset LastModified)> ListAsync(string prefix, CancellationToken cancellationToken);
}

/// <summary>S3-compatible object storage through the AWS SDK for .NET (Apache-2.0).</summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public S3ObjectStorage(AttachmentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _bucket = options.Bucket ?? throw new InvalidOperationException("Attachments:S3:Bucket is required.");
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            config.ServiceURL = options.Endpoint;
            config.AuthenticationRegion = options.Region;
        }
        else if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        }

        _client = string.IsNullOrEmpty(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/octet-stream",
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<(string Key, DateTimeOffset LastModified)> ListAsync(
        string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                yield return (item.Key, item.LastModified is { } at ? new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.MinValue);
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Contents waiting to be written to object storage when the scope's changes are saved.</summary>
public sealed class PendingObjectUploads
{
    internal Dictionary<string, byte[]> Uploads { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Attachment content in S3-compatible object storage (task F6). The attachment's row in
/// <c>ticket_attachment_contents</c> stays, with empty content, as the pointer (branding images
/// keep their content there): row-level
/// security decides who may read the file and every clean-up that deletes rows keeps working
/// (the objects of deleted rows are removed by <see cref="ObjectStorageSweeper"/>). Rows with
/// content (stored before the switch to S3) are read from the database as before. Objects are
/// written before the transaction commits (<see cref="ObjectUploadInterceptor"/>), under the
/// tenant's own key prefix.
/// </summary>
public sealed class ObjectAttachmentStore(
    ZelvoDbContext db, ITenantContext tenant, IObjectStorage storage, AttachmentStorageOptions options, PendingObjectUploads pending) : IAttachmentStore
{
    public void Add(Guid attachmentId, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Attachments need the request's tenant.");
        // Branding images (F1) stay in the database: they are small and read for every page and
        // email by code that has only the database at hand.
        if (db.ChangeTracker.Entries<Zelvo.Data.Tickets.TicketAttachment>().Any(e => e.Entity.Id == attachmentId && e.Entity.BrandingSlot is not null))
        {
            db.TicketAttachmentContents.Add(new Zelvo.Data.Tickets.TicketAttachmentContent { AttachmentId = attachmentId, Content = content });
            return;
        }

        db.TicketAttachmentContents.Add(new Zelvo.Data.Tickets.TicketAttachmentContent { AttachmentId = attachmentId, Content = [] });
        pending.Uploads[options.Key(tenantId, attachmentId)] = content;
    }

    public async Task<byte[]?> ReadAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        var row = await db.TicketAttachmentContents.AsNoTracking().Where(c => c.AttachmentId == attachmentId)
            .Select(c => new { c.TenantId, c.Content }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return row.Content.Length > 0 ? row.Content : await storage.GetAsync(options.Key(row.TenantId, attachmentId), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        var tracked = db.ChangeTracker.Entries<Zelvo.Data.Tickets.TicketAttachmentContent>().FirstOrDefault(e => e.Entity.AttachmentId == attachmentId);
        if (tracked is not null)
        {
            if (tracked.State == EntityState.Added)
            {
                tracked.State = EntityState.Detached;
                pending.Uploads.Remove(options.Key(tracked.Entity.TenantId == Guid.Empty ? tenant.TenantId ?? Guid.Empty : tracked.Entity.TenantId, attachmentId));
                return;
            }

            tracked.State = EntityState.Deleted;
            return;
        }

        // The row goes; its object is removed by the sweeper once the row is gone for good.
        await db.TicketAttachmentContents.Where(c => c.AttachmentId == attachmentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Writes the scope's pending attachment objects before the database transaction commits.</summary>
public sealed class ObjectUploadInterceptor(PendingObjectUploads pending, IObjectStorage storage) : SaveChangesInterceptor, IZelvoSaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await UploadAsync(cancellationToken).ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        UploadAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    private async Task UploadAsync(CancellationToken cancellationToken)
    {
        foreach (var (key, content) in pending.Uploads.ToList())
        {
            await storage.PutAsync(key, content, cancellationToken).ConfigureAwait(false);
            pending.Uploads.Remove(key);
        }
    }
}

/// <summary>
/// Removes attachment objects whose row is gone (deleted files, erased people, retention, saves
/// that failed after the upload), once a day, for every tenant (task F6).
/// </summary>
public sealed partial class ObjectStorageSweeper(
    IServiceScopeFactory scopes, IObjectStorage storage, AttachmentStorageOptions options, TimeProvider time, ILogger<ObjectStorageSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                var removed = await SweepAsync(stoppingToken).ConfigureAwait(false);
                LogSwept(logger, removed);
            }
#pragma warning disable CA1031 // A failed sweep is tried again tomorrow; nothing depends on it.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogSweepFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Removes the orphaned objects and returns how many.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid Id, string Slug)> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_attachment_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<(Guid, string)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add((reader.GetGuid(0), reader.GetString(1)));
            }

            tenants = list;
        }

        var cutoff = time.GetUtcNow() - options.OrphanAge;
        var removed = 0;
        foreach (var (tenantId, slug) in tenants)
        {
            var prefix = $"{options.Prefix}{tenantId:D}/";
            var candidates = new Dictionary<Guid, string>();
            await foreach (var (key, lastModified) in storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                if (lastModified < cutoff && Guid.TryParse(key[prefix.Length..], out var id))
                {
                    candidates[id] = key;
                }
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            foreach (var chunk in candidates.Keys.Chunk(500))
            {
                var existing = await db.TicketAttachmentContents.AsNoTracking().Where(c => chunk.Contains(c.AttachmentId))
                    .Select(c => c.AttachmentId).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var id in chunk.Except(existing))
                {
                    await storage.DeleteAsync(candidates[id], cancellationToken).ConfigureAwait(false);
                    removed++;
                }
            }
        }

        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Attachment object storage swept: {Removed} orphaned objects removed")]
    private static partial void LogSwept(ILogger logger, int removed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The attachment object storage sweep failed; it runs again tomorrow")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}

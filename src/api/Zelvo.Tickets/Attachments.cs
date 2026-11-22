using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Data;
using Zelvo.Data.Tickets;

namespace Zelvo.Tickets;

/// <summary>
/// Where attachment content lives. PostgreSQL in Phase 1 (product owner decision 2026-11-22),
/// isolated by row-level security and included in database backups; S3-compatible storage for
/// medium and large installations is added behind this interface in task F6.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>Stages the content of <paramref name="attachmentId"/>; saved with the caller's <c>SaveChangesAsync</c>.</summary>
    void Add(Guid attachmentId, byte[] content);

    /// <summary>The content, or null when there is none (deleted, or never stored).</summary>
    Task<byte[]?> ReadAsync(Guid attachmentId, CancellationToken cancellationToken);

    /// <summary>Stages removal of the content; saved with the caller's <c>SaveChangesAsync</c>.</summary>
    Task RemoveAsync(Guid attachmentId, CancellationToken cancellationToken);
}

/// <summary>Attachment content in the <c>ticket_attachment_contents</c> table, under the tenant's row-level security.</summary>
public sealed class PostgresAttachmentStore(ZelvoDbContext db) : IAttachmentStore
{
    /// <inheritdoc />
    public void Add(Guid attachmentId, byte[] content) =>
        db.TicketAttachmentContents.Add(new TicketAttachmentContent { AttachmentId = attachmentId, Content = content });

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(Guid attachmentId, CancellationToken cancellationToken) =>
        await db.TicketAttachmentContents.AsNoTracking().Where(c => c.AttachmentId == attachmentId)
            .Select(c => c.Content).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task RemoveAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        var tracked = db.ChangeTracker.Entries<TicketAttachmentContent>().FirstOrDefault(e => e.Entity.AttachmentId == attachmentId);
        if (tracked is not null)
        {
            tracked.State = tracked.State == EntityState.Added ? EntityState.Detached : EntityState.Deleted;
            return;
        }

        // Delete without loading the content.
        await db.TicketAttachmentContents.Where(c => c.AttachmentId == attachmentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Malware scanning of uploaded files (<c>AttachmentScanning</c> configuration).</summary>
public sealed class AttachmentScanningOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "AttachmentScanning";

    /// <summary>On by default (product owner decision 2026-11-22). When off, files are marked "not scanned" and can be downloaded.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ClamAV daemon host.</summary>
    public string Host { get; set; } = "clamav";

    /// <summary>ClamAV daemon port.</summary>
    public int Port { get; set; } = 3310;

    /// <summary>Longest a scan may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the worker looks for files to scan.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>What a malware scan found.</summary>
/// <param name="Clean">No malware found.</param>
/// <param name="Signature">Name of the malware found, when not clean.</param>
public sealed record ScanVerdict(bool Clean, string? Signature);

/// <summary>Scans file content for malware.</summary>
public interface IMalwareScanner
{
    /// <summary>Scans <paramref name="content"/>. Throws <see cref="MalwareScannerException"/> when the scanner cannot give an answer.</summary>
    Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

/// <summary>The scanner could not scan the file (unreachable, timed out, or an error).</summary>
public sealed class MalwareScannerException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MalwareScannerException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MalwareScannerException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public MalwareScannerException(string message) : base(message)
    {
    }
}

/// <summary>
/// ClamAV daemon (clamd) client using the <c>INSTREAM</c> command: the content is sent in chunks
/// of at most 64 KiB, each prefixed with its length (4 bytes, big-endian), ended by a zero length.
/// clamd answers <c>stream: OK</c>, <c>stream: &lt;name&gt; FOUND</c> or <c>... ERROR</c>.
/// </summary>
public sealed class ClamAvScanner(IOptions<AttachmentScanningOptions> options) : IMalwareScanner
{
    private const int ChunkSize = 64 * 1024;

    /// <inheritdoc />
    public async Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        string reply;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            var length = new byte[4];
            for (var offset = 0; offset < content.Length; offset += ChunkSize)
            {
                var chunk = content.Slice(offset, Math.Min(ChunkSize, content.Length - offset));
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)chunk.Length);
                await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(chunk, timeout.Token).ConfigureAwait(false);
            }

            BinaryPrimitives.WriteUInt32BigEndian(length, 0);
            await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            reply = await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new MalwareScannerException("The malware scanner could not be reached or did not answer in time.", ex);
        }

        return Parse(reply);
    }

    /// <summary>Reads clamd's answer to a scan.</summary>
    internal static ScanVerdict Parse(string reply)
    {
        var text = reply.TrimEnd('\0', '\n', '\r').Trim();
        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var start = text.IndexOf(':', StringComparison.Ordinal) + 1;
            var signature = text[start..^" FOUND".Length].Trim();
            return new ScanVerdict(false, signature.Length == 0 ? "unknown" : signature[..Math.Min(signature.Length, 200)]);
        }

        if (text.EndsWith(": OK", StringComparison.Ordinal))
        {
            return new ScanVerdict(true, null);
        }

        // For example "INSTREAM size limit exceeded. ERROR": clamd's StreamMaxLength is too small.
        throw new MalwareScannerException($"The malware scanner refused the file: {text[..Math.Min(text.Length, 200)]}");
    }

    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var reply = new StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            reply.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (buffer.AsSpan(0, read).Contains((byte)0))
            {
                break;
            }
        }

        return reply.ToString();
    }
}

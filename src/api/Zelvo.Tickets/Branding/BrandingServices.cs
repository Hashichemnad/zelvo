using System.Globalization;
using System.Text.RegularExpressions;
using DnsClient;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Branding;
using Zelvo.Data.Tickets;

namespace Zelvo.Tickets.Branding;

/// <summary>An image of the branding, ready to serve or embed.</summary>
/// <param name="Id">The attachment.</param>
/// <param name="ContentType">Its type (PNG, JPEG, WebP or ICO).</param>
/// <param name="Content">Its bytes.</param>
/// <param name="Sha256">Its hash, for caching.</param>
public sealed record BrandingImage(Guid Id, string ContentType, byte[] Content, string Sha256);

/// <summary>Reads the current tenant's branding (module 11). Row-level security limits it to the tenant.</summary>
public static class BrandingReader
{
    /// <summary>The published branding, or Zelvo's default look.</summary>
    public static async Task<BrandingValue> PublishedAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var json = await db.TenantBrandings.AsNoTracking().Select(b => b.Published).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return BrandingValue.Read(json);
    }

    /// <summary>The image of <paramref name="slot"/> in <paramref name="branding"/>, when it was scanned clean.</summary>
    public static async Task<BrandingImage?> ImageAsync(ZelvoDbContext db, BrandingValue branding, BrandingSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(branding);
        if (!branding.Images.TryGetValue(slot, out var id))
        {
            return null;
        }

        var attachment = await db.TicketAttachments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == id && a.BrandingSlot == slot && a.ScanStatus == AttachmentScanStatus.Clean, cancellationToken).ConfigureAwait(false);
        if (attachment is null)
        {
            return null;
        }

        var content = await db.TicketAttachmentContents.AsNoTracking().Where(c => c.AttachmentId == id).Select(c => c.Content)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return content is null ? null : new BrandingImage(id, attachment.ContentType, content, attachment.Sha256);
    }
}

/// <summary>Host names of custom domains and sender domains.</summary>
public static partial class HostNames
{
    /// <summary>The host in lower case without a trailing dot, or null when it is not a public DNS name of two or more labels.</summary>
    public static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var value = host.Trim().TrimEnd('.').ToLowerInvariant();
        try
        {
            // International names (for example Arabic domains) become their ASCII (punycode) form.
            value = new IdnMapping().GetAscii(value);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return value.Length <= TenantDomain.HostMaxLength && HostPattern().IsMatch(value) && !value.EndsWith(".local", StringComparison.Ordinal)
            && !value.EndsWith(".internal", StringComparison.Ordinal) && !value.EndsWith(".localhost", StringComparison.Ordinal)
            ? value
            : null;
    }

    [GeneratedRegex("^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex HostPattern();
}

/// <summary>DNS lookups for the guided checks (custom domains, SPF, DKIM, DMARC). Tests replace it.</summary>
public interface IDnsLookup
{
    /// <summary>The TXT records of <paramref name="name"/> (each record's strings joined); empty when there are none.</summary>
    Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken);
}

/// <summary>DNS through the installation's resolvers with DnsClient (Apache 2.0); no cache, so a check sees changes at once.</summary>
public sealed class SystemDnsLookup : IDnsLookup
{
    private readonly LookupClient _client = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 });

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken).ConfigureAwait(false);
            return [.. result.Answers.TxtRecords().Select(r => string.Concat(r.Text))];
        }
        catch (DnsResponseException)
        {
            return [];
        }
    }
}

using System.Net;
using System.Net.Sockets;

namespace Zelvo.Integrations;

/// <summary>Why an outgoing request was not made.</summary>
public sealed class OutboundRequestBlockedException(string code, string message) : Exception(message)
{
    /// <summary>Stable code for the UI and the delivery log, for example <c>address-not-allowed</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Which addresses outgoing requests (webhooks) may reach, against server-side request forgery
/// (product owner decision 2026-11-22): HTTPS only; never loopback, link-local (which includes
/// cloud metadata at 169.254.169.254), unspecified, multicast or reserved addresses; private
/// networks only when the operator lists them in <see cref="WebhookOptions.AllowedPrivateNetworks"/>.
/// The check runs on the address actually connected to, after DNS, so a name that resolves to an
/// internal address (or changes to one later) is refused too.
/// </summary>
public sealed class OutboundAddressPolicy(WebhookOptions options)
{
    // Never reachable, even when listed: this host, link-local and cloud metadata, special ranges.
    private static readonly IPNetwork[] AlwaysBlocked =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("fd00:ec2::254/128"),
    ];

    // Internal networks: reachable only when the operator allows them.
    private static readonly IPNetwork[] Private =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("fc00::/7"),
    ];

    /// <summary>Tests only: lets requests reach this computer (test receivers listen on loopback).</summary>
    internal bool AllowLoopbackForTests { get; init; }

    /// <summary>Checks the form of a receiver URL when it is saved. Returns an error code, or null when acceptable.</summary>
    public string? CheckUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return "url-invalid";
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && !(options.AllowInsecureHttp && parsed.Scheme == Uri.UriSchemeHttp))
        {
            return "url-not-https";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            // Credentials in the URL would be stored and logged in clear.
            return "url-invalid";
        }

        if (parsed.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(parsed.IdnHost.Trim('[', ']'), out var literal) && !IsAllowed(literal))
        {
            return "address-not-allowed";
        }

        if (!AllowLoopbackForTests && (parsed.IsLoopback || parsed.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || parsed.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return "address-not-allowed";
        }

        uri = parsed;
        return null;
    }

    /// <summary>Whether a request may connect to <paramref name="address"/>.</summary>
    public bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (AllowLoopbackForTests && IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (AlwaysBlocked.Any(n => n.Contains(address)) || IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
        {
            return false;
        }

        if (address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        return !Private.Any(n => n.Contains(address)) || options.AllowedPrivateNetworks.Any(n => n.Contains(address));
    }

    /// <summary>
    /// Connects to <paramref name="host"/> on <paramref name="port"/>, using only addresses the
    /// policy allows (for <see cref="SocketsHttpHandler.ConnectCallback"/>).
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var allowed = addresses.Where(IsAllowed).ToList();
        if (allowed.Count == 0)
        {
            throw new OutboundRequestBlockedException("address-not-allowed",
                addresses.Length == 0 ? $"{host} has no address." : $"{host} resolves only to addresses that webhooks may not reach.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync([.. allowed], port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

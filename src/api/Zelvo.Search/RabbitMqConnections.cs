using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using RabbitMQ.Client;

namespace Zelvo.Search;

/// <summary>
/// RabbitMQ connections of Zelvo's services. <c>amqps://</c> addresses use TLS 1.3; with a CA file
/// (<c>RabbitMq:CaFile</c>) the broker's certificate must chain to that CA and match the host name.
/// </summary>
public static class RabbitMqConnections
{
    /// <summary>A connection factory for <paramref name="address"/> (user, password and virtual host in the address).</summary>
    public static ConnectionFactory CreateFactory(Uri address, string clientName, string? caFile)
    {
        ArgumentNullException.ThrowIfNull(address);
        var factory = new ConnectionFactory { Uri = address, ClientProvidedName = clientName };
        if (factory.Ssl.Enabled)
        {
            factory.Ssl.Version = System.Security.Authentication.SslProtocols.Tls13;
            factory.Ssl.ServerName = address.Host;
            if (caFile is { Length: > 0 })
            {
                var ca = X509CertificateLoader.LoadCertificateFromFile(caFile);
                factory.Ssl.CertificateValidationCallback = (_, certificate, _, errors) => TrustedBy(ca, certificate, errors);
            }
        }
        else if (!address.IsLoopback)
        {
            // Plain AMQP would send the password and messages in clear text (TLS in transit).
            throw new InvalidOperationException("RabbitMQ must be reached with amqps:// (TLS) unless it runs on this computer.");
        }

        return factory;
    }

    /// <summary>The certificate chains to <paramref name="ca"/> and matches the host name.</summary>
    internal static bool TrustedBy(X509Certificate2 ca, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }
}

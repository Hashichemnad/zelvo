using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Zelvo.Search;

namespace Zelvo.Api.Tests;

/// <summary>OpenSearch and RabbitMQ clients with security on (A5): credentials only over TLS, private CAs.</summary>
public sealed class ServiceTransportSecurityTests
{
    [Fact]
    public async Task The_opensearch_client_signs_in_with_its_own_user()
    {
        var handler = new RecordingHandler();
        using var client = new OpenSearchClient(Options.Create(new SearchOptions
        {
            OpenSearch = new Uri("https://opensearch:9200/"),
            Username = "zelvo-api",
            Password = "test-only-password",
        }), handler);

        Assert.Equal("green", await client.ClusterStatusAsync(TestContext.Current.CancellationToken));

        var authorization = Assert.Single(handler.Requests).Headers.Authorization!;
        Assert.Equal("Basic", authorization.Scheme);
        Assert.Equal("zelvo-api:test-only-password", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter!)));
    }

    [Fact]
    public void The_opensearch_client_never_sends_credentials_over_plain_http_to_another_host()
    {
        var options = Options.Create(new SearchOptions { OpenSearch = new Uri("http://opensearch:9200/"), Username = "zelvo-api", Password = "x" });

        Assert.Throws<InvalidOperationException>(() => new OpenSearchClient(options, new RecordingHandler()));
    }

    [Fact]
    public void RabbitMQ_uses_tls_1_3_for_amqps_and_refuses_plain_amqp_to_another_host()
    {
        var factory = RabbitMqConnections.CreateFactory(new Uri("amqps://zelvo-worker:x@rabbitmq:5671/zelvo"), "test", null);

        Assert.True(factory.Ssl.Enabled);
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls13, factory.Ssl.Version);
        Assert.Equal("rabbitmq", factory.Ssl.ServerName);
        Assert.Equal("zelvo", factory.VirtualHost);
        Assert.Throws<InvalidOperationException>(() => RabbitMqConnections.CreateFactory(new Uri("amqp://u:p@rabbitmq:5672/"), "test", null));
        Assert.False(RabbitMqConnections.CreateFactory(new Uri("amqp://u:p@127.0.0.1:5672/"), "test", null).Ssl.Enabled);
    }

    [Fact]
    public void RabbitMQ_trusts_only_certificates_from_the_configured_ca()
    {
        using var ca = CreateCa("Test CA");
        using var otherCa = CreateCa("Other CA");
        using var server = Issue(ca, "rabbitmq");
        using var stranger = Issue(otherCa, "rabbitmq");
        using var caPublic = X509CertificateLoader.LoadCertificate(ca.Export(X509ContentType.Cert));

        Assert.True(RabbitMqConnections.TrustedBy(caPublic, server, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(RabbitMqConnections.TrustedBy(caPublic, stranger, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(RabbitMqConnections.TrustedBy(caPublic, server, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(RabbitMqConnections.TrustedBy(caPublic, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    private static X509Certificate2 CreateCa(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 Issue(X509Certificate2 ca, string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        return request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":"green"}""") });
        }
    }
}

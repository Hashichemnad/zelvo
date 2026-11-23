using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace Zelvo.Integrations;

/// <summary>What happened when a webhook was sent.</summary>
/// <param name="Succeeded">The receiver answered with a 2xx status.</param>
/// <param name="StatusCode">The receiver's status, if it answered.</param>
/// <param name="DurationMs">How long the attempt took.</param>
/// <param name="ResponseBody">The start of the answer (at most 4 KB), not yet masked.</param>
/// <param name="ResponseContentType">Content type of the answer.</param>
/// <param name="Error">Why it failed: a code such as <c>timeout</c> and a short explanation. Never holds the payload.</param>
public sealed record WebhookSendResult(bool Succeeded, int? StatusCode, int DurationMs, string? ResponseBody, string? ResponseContentType, string? Error);

/// <summary>
/// Posts signed webhooks. Connections go only to addresses that <see cref="OutboundAddressPolicy"/>
/// allows (checked on the resolved address, so DNS tricks do not help), redirects are not
/// followed, no system proxy is used, and every attempt has a time limit.
/// </summary>
public sealed class WebhookSender : IDisposable
{
    /// <summary>Most of an answer read and kept.</summary>
    public const int MaxResponseBytes = 4096;

    private readonly HttpClient _http;
    private readonly WebhookOptions _options;

    public WebhookSender(WebhookOptions options, OutboundAddressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        _options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = options.Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, cancellationToken) =>
                policy.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken),
            SslOptions = { EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 },
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zelvo-Webhooks", "1.0"));
    }

    /// <summary>Sends <paramref name="body"/> to <paramref name="url"/>, signed with <paramref name="secrets"/>.</summary>
    public async Task<WebhookSendResult> SendAsync(
        Uri url, Guid eventId, string eventType, string body, IReadOnlyList<string> secrets, string environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var messageId = "msg_" + eventId.ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("webhook-id", messageId);
        request.Headers.Add("webhook-timestamp", timestamp.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("webhook-signature", WebhookSigner.Sign(messageId, timestamp, body, secrets));
        request.Headers.Add("Zelvo-Event", eventType);
        request.Headers.Add("Zelvo-Environment", environment);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var excerpt = await ReadExcerptAsync(response, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var ok = response.IsSuccessStatusCode;
            return new WebhookSendResult(ok, status, Elapsed(watch), excerpt, response.Content.Headers.ContentType?.MediaType,
                ok ? null : status is >= 300 and < 400 ? $"redirect: the receiver answered {status}; redirects are not followed" : $"http-{status}: the receiver answered {status}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(watch, $"timeout: no answer within {_options.Timeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex) when (Blocked(ex) is { } blocked)
        {
            return Failure(watch, $"{blocked.Code}: {blocked.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Failure(watch, $"connection-failed: {Shorten(ex.InnerException?.Message ?? ex.Message)}");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private static OutboundRequestBlockedException? Blocked(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OutboundRequestBlockedException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    private static async Task<string?> ReadExcerptAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxResponseBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            return read == 0 ? null : Encoding.UTF8.GetString(buffer, 0, read);
        }
    }

    private static WebhookSendResult Failure(Stopwatch watch, string error) => new(false, null, Elapsed(watch), null, null, Shorten(error));

    private static int Elapsed(Stopwatch watch) => (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);

    private static string Shorten(string value) => value.Length <= 500 ? value : value[..500];
}

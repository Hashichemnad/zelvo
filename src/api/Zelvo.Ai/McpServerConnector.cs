using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Zelvo.Integrations;

namespace Zelvo.Ai;

/// <summary>Where a customer's MCP server is and how to sign in to it, with the credential decrypted for this call only.</summary>
/// <param name="Url">The server's Streamable HTTP endpoint.</param>
/// <param name="AuthHeader">Header for the credential; <c>Authorization</c> sends <c>Bearer &lt;secret&gt;</c>. Null for none.</param>
/// <param name="Secret">The credential.</param>
public sealed record McpServerEndpoint(Uri Url, string? AuthHeader, string? Secret)
{
    /// <inheritdoc />
    public override string ToString() => Url.ToString();
}

/// <summary>A tool a customer's MCP server offers.</summary>
/// <param name="Name">Tool name.</param>
/// <param name="Title">Display name, if given.</param>
/// <param name="Description">What it does, as the server describes it (untrusted).</param>
/// <param name="InputSchema">JSON Schema of the arguments.</param>
/// <param name="ReadOnlyHint">The server says the tool does not change anything (a hint only; administrators decide).</param>
public sealed record McpRemoteTool(string Name, string? Title, string? Description, JsonElement InputSchema, bool ReadOnlyHint);

/// <summary>What a customer's tool answered.</summary>
/// <param name="IsError">The tool reported an error.</param>
/// <param name="Text">Its text content, cut to <see cref="McpServerConnector.MaxResultLength"/> characters.</param>
public sealed record McpRemoteToolResult(bool IsError, string Text);

/// <summary>A connection to a customer's MCP server failed.</summary>
/// <param name="code">Stable code: <c>address-not-allowed</c>, <c>connection-failed</c>, <c>timeout</c>, <c>server-auth-failed</c>, <c>server-error</c>.</param>
/// <param name="message">Short explanation.</param>
public sealed class McpConnectionException(string code, string message) : Exception(message)
{
    /// <summary>Stable code for the UI and the audit log.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Zelvo's client for customers' own MCP servers (module 9): Streamable HTTP through the official
/// MCP SDK, but over Zelvo's own connections, so every connection goes only to addresses the
/// outgoing-request policy allows (checked after DNS, the AI settings of the installation), no
/// redirects are followed, no system proxy is used and each call has the AI time limit.
/// </summary>
public sealed class McpServerConnector : IDisposable
{
    /// <summary>Longest tool answer kept, in characters.</summary>
    public const int MaxResultLength = 32 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the connector; <paramref name="policy"/> decides which addresses may be reached.</summary>
    public McpServerConnector(AiOptions options, OutboundAddressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        _timeout = options.Timeout;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, cancellationToken) => policy.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken),
            SslOptions = { EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 },
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zelvo-AI", "1.0"));
    }

    /// <summary>The outgoing-request policy, also used to check addresses when they are saved.</summary>
    public OutboundAddressPolicy Policy { get; }

    /// <summary>Connects and lists the server's tools (also the connection test).</summary>
    public Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(McpServerEndpoint endpoint, CancellationToken cancellationToken) =>
        WithClientAsync<IReadOnlyList<McpRemoteTool>>(endpoint, async (client, token) =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: token).ConfigureAwait(false);
            return [.. tools.Select(t => new McpRemoteTool(
                t.Name, t.Title, t.Description, t.ProtocolTool.InputSchema, t.ProtocolTool.Annotations?.ReadOnlyHint == true))];
        }, cancellationToken);

    /// <summary>Calls one tool with <paramref name="arguments"/> (a JSON object).</summary>
    public Task<McpRemoteToolResult> CallToolAsync(McpServerEndpoint endpoint, string tool, JsonElement arguments, CancellationToken cancellationToken) =>
        WithClientAsync(endpoint, async (client, token) =>
        {
            var args = arguments.ValueKind == JsonValueKind.Object
                ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            var result = await client.CallToolAsync(tool, args, cancellationToken: token).ConfigureAwait(false);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            if (text.Length == 0 && result.StructuredContent is { } structured)
            {
                text = structured.ToString();
            }

            return new McpRemoteToolResult(result.IsError == true, text.Length <= MaxResultLength ? text : string.Concat(text.AsSpan(0, MaxResultLength - 1), "…"));
        }, cancellationToken);

    private async Task<T> WithClientAsync<T>(McpServerEndpoint endpoint, Func<McpClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint.Url,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "zelvo",
            ConnectionTimeout = TimeSpan.FromSeconds(15),
            AdditionalHeaders = Headers(endpoint),
        };
        try
        {
            var transport = new HttpClientTransport(options, _http, NullLoggerFactory.Instance, ownsHttpClient: false);
            await using var client = await McpClient.CreateAsync(transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = "zelvo", Version = "1.0" } },
                NullLoggerFactory.Instance, timeout.Token).ConfigureAwait(false);
            return await action(client, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new McpConnectionException("timeout", $"The MCP server did not answer within {_timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (Find<OutboundRequestBlockedException>(ex) is { } blocked)
        {
            throw new McpConnectionException("address-not-allowed", blocked.Message);
        }
        catch (Exception ex) when (Find<HttpRequestException>(ex) is { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
        {
            throw new McpConnectionException("server-auth-failed", "The MCP server refused the credential.");
        }
        catch (Exception ex) when (Find<HttpRequestException>(ex) is { } http)
        {
            throw new McpConnectionException(http.StatusCode is null ? "connection-failed" : "server-error",
                http.StatusCode is { } status ? $"The MCP server answered {(int)status}." : "Zelvo could not connect to the MCP server.");
        }
        catch (Exception ex) when (ex is McpException or JsonException or InvalidOperationException or IOException)
        {
            throw new McpConnectionException("server-error", "The MCP server's answer was not valid MCP.");
        }
    }

    private static Dictionary<string, string>? Headers(McpServerEndpoint endpoint)
    {
        if (endpoint.AuthHeader is not { Length: > 0 } header || endpoint.Secret is not { Length: > 0 } secret)
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [header] = header.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " + secret : secret,
        };
    }

    private static TException? Find<TException>(Exception? exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException found)
            {
                return found;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

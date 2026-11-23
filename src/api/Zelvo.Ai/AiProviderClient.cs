using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zelvo.Data.Ai;
using Zelvo.Integrations;

namespace Zelvo.Ai;

/// <summary>Where and how to reach a provider, with the API key decrypted for this call only.</summary>
/// <param name="Kind">Which API it speaks.</param>
/// <param name="Endpoint">Base address, or null for the provider's public address.</param>
/// <param name="ApiVersion">Azure OpenAI: <c>api-version</c> for deployment addresses.</param>
/// <param name="ApiKey">The API key, if any.</param>
public sealed record AiProviderConnection(AiProviderKind Kind, Uri? Endpoint, string? ApiVersion, string? ApiKey)
{
    /// <inheritdoc />
    public override string ToString() => $"{Kind} {Endpoint}";
}

/// <summary>Who speaks in a message to the model.</summary>
public enum AiRole
{
    /// <summary>The person (or data from them).</summary>
    User,

    /// <summary>The model's earlier answers.</summary>
    Assistant,
}

/// <summary>One message of a conversation.</summary>
/// <param name="Role">Who speaks.</param>
/// <param name="Text">What is said.</param>
public sealed record AiMessage(AiRole Role, string Text);

/// <summary>A call to a model as the provider receives it (already masked and guarded).</summary>
/// <param name="Model">Model id (Azure OpenAI: deployment name).</param>
/// <param name="System">System instructions.</param>
/// <param name="Messages">The conversation; the last message is the user's.</param>
/// <param name="MaxOutputTokens">Most tokens the answer may have.</param>
/// <param name="Json">The answer must be a JSON object.</param>
public sealed record AiProviderRequest(string Model, string System, IReadOnlyList<AiMessage> Messages, int MaxOutputTokens, bool Json);

/// <summary>What the provider answered.</summary>
/// <param name="Text">The answer text.</param>
/// <param name="InputTokens">Input tokens counted by the provider.</param>
/// <param name="OutputTokens">Output tokens counted by the provider, including reasoning tokens.</param>
/// <param name="Truncated">The answer stopped at <see cref="AiProviderRequest.MaxOutputTokens"/>.</param>
public sealed record AiProviderResponse(string Text, int InputTokens, int OutputTokens, bool Truncated);

/// <summary>A provider call failed.</summary>
/// <param name="code">Stable code: <c>provider-auth-failed</c>, <c>provider-rate-limited</c>, <c>provider-unavailable</c>, <c>provider-rejected</c>, <c>provider-invalid-response</c>, <c>timeout</c>, <c>address-not-allowed</c>, <c>connection-failed</c>.</param>
/// <param name="message">Short explanation, without the prompt.</param>
/// <param name="statusCode">The provider's HTTP status, if it answered.</param>
public sealed class AiProviderException(string code, string message, int? statusCode = null) : Exception(message)
{
    /// <summary>Stable code for the UI and the AI log.</summary>
    public string Code { get; } = code;

    /// <summary>The provider's HTTP status, if it answered.</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// Speaks the three provider APIs: Anthropic Messages, OpenAI Chat Completions (also Azure OpenAI
/// and OpenAI-compatible endpoints) and Gemini generateContent. Thin clients of Zelvo's own, so
/// every call goes through the outgoing-request policy and time limit (no vendor SDKs).
/// Connections go only to addresses <see cref="OutboundAddressPolicy"/> allows, checked after DNS;
/// redirects are not followed and no system proxy is used.
/// </summary>
public sealed class AiProviderClient : IDisposable
{
    /// <summary>Anthropic's public API.</summary>
    public static readonly Uri AnthropicEndpoint = new("https://api.anthropic.com/");

    /// <summary>OpenAI's public API.</summary>
    public static readonly Uri OpenAIEndpoint = new("https://api.openai.com/v1/");

    /// <summary>Google's Gemini API.</summary>
    public static readonly Uri GeminiEndpoint = new("https://generativelanguage.googleapis.com/v1beta/");

    /// <summary>Anthropic API version header.</summary>
    public const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly AiOptions _options;

    /// <summary>Creates the client; <paramref name="policy"/> decides which addresses may be reached.</summary>
    public AiProviderClient(AiOptions options, OutboundAddressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        _options = options;
        Policy = policy;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, cancellationToken) =>
                policy.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken),
            SslOptions = { EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 },
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zelvo-AI", "1.0"));
    }

    /// <summary>The outgoing-request policy, also used to check endpoints when they are saved.</summary>
    public OutboundAddressPolicy Policy { get; }

    /// <summary>Sends one call to the model and returns its answer.</summary>
    public async Task<AiProviderResponse> CompleteAsync(AiProviderConnection connection, AiProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        var (url, body) = connection.Kind switch
        {
            AiProviderKind.Anthropic => (Combine(connection.Endpoint ?? AnthropicEndpoint, "v1/messages"), AnthropicBody(request)),
            AiProviderKind.Gemini => (Combine(connection.Endpoint ?? GeminiEndpoint, $"models/{Uri.EscapeDataString(request.Model)}:generateContent"), GeminiBody(request)),
            AiProviderKind.AzureOpenAI => (AzureUrl(connection, request.Model, "chat/completions"), ChatBody(request, connection.Kind)),
            AiProviderKind.OpenAI => (Combine(connection.Endpoint ?? OpenAIEndpoint, "chat/completions"), ChatBody(request, connection.Kind)),
            AiProviderKind.OpenAICompatible => (Combine(RequireEndpoint(connection), "chat/completions"), ChatBody(request, connection.Kind)),
            _ => throw new ArgumentOutOfRangeException(nameof(connection), connection.Kind, "Unknown provider."),
        };

        var json = await SendAsync(HttpMethod.Post, url, connection, body, cancellationToken).ConfigureAwait(false);
        return connection.Kind switch
        {
            AiProviderKind.Anthropic => ParseAnthropic(json),
            AiProviderKind.Gemini => ParseGemini(json),
            _ => ParseChat(json),
        };
    }

    /// <summary>The models the provider offers, where its API lists them; empty when it does not.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(AiProviderConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Uri? url = connection.Kind switch
        {
            AiProviderKind.Anthropic => Combine(connection.Endpoint ?? AnthropicEndpoint, "v1/models?limit=1000"),
            AiProviderKind.Gemini => Combine(connection.Endpoint ?? GeminiEndpoint, "models?pageSize=1000"),
            AiProviderKind.OpenAI => Combine(connection.Endpoint ?? OpenAIEndpoint, "models"),
            AiProviderKind.OpenAICompatible => Combine(RequireEndpoint(connection), "models"),
            // Azure deployments are named by the customer; only the v1 API lists models.
            AiProviderKind.AzureOpenAI when string.IsNullOrEmpty(connection.ApiVersion) => AzureUrl(connection, null, "models"),
            _ => null,
        };
        if (url is null)
        {
            return [];
        }

        var json = await SendAsync(HttpMethod.Get, url, connection, null, cancellationToken).ConfigureAwait(false);
        var models = connection.Kind == AiProviderKind.Gemini
            ? (json["models"] as JsonArray ?? [])
                .Where(m => (m?["supportedGenerationMethods"] as JsonArray)?.Any(x => (string?)x == "generateContent") ?? true)
                .Select(m => ((string?)m?["name"])?.Replace("models/", string.Empty, StringComparison.Ordinal))
            : (json["data"] as JsonArray ?? []).Select(m => (string?)m?["id"]);
        return [.. models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<JsonNode> SendAsync(HttpMethod method, Uri url, AiProviderConnection connection, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        Authenticate(request, connection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var text = await ReadAsync(response, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var detail = ErrorDetail(text);
                throw status switch
                {
                    401 or 403 => new AiProviderException("provider-auth-failed", $"The provider refused the API key ({status}).{detail}", status),
                    429 => new AiProviderException("provider-rate-limited", $"The provider is limiting requests or the account has no credit left ({status}).{detail}", status),
                    >= 500 => new AiProviderException("provider-unavailable", $"The provider is not available ({status}).{detail}", status),
                    >= 300 and < 400 => new AiProviderException("provider-rejected", $"The provider answered with a redirect ({status}); redirects are not followed. Check the endpoint.", status),
                    _ => new AiProviderException("provider-rejected", $"The provider refused the request ({status}).{detail}", status),
                };
            }

            try
            {
                return JsonNode.Parse(text ?? string.Empty) ?? throw new AiProviderException("provider-invalid-response", "The provider's answer is empty.", status);
            }
            catch (JsonException)
            {
                throw new AiProviderException("provider-invalid-response", "The provider's answer is not JSON. Check the endpoint.", status);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException("timeout", $"No answer within {_options.Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex) when (Blocked(ex) is { } blocked)
        {
            throw new AiProviderException(blocked.Code, blocked.Message);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException("connection-failed", Shorten("Could not connect: " + (ex.InnerException?.Message ?? ex.Message)));
        }
    }

    private static void Authenticate(HttpRequestMessage request, AiProviderConnection connection)
    {
        if (string.IsNullOrEmpty(connection.ApiKey))
        {
            if (connection.Kind == AiProviderKind.Anthropic)
            {
                request.Headers.Add("anthropic-version", AnthropicVersion);
            }

            return;
        }

        switch (connection.Kind)
        {
            case AiProviderKind.Anthropic:
                request.Headers.Add("x-api-key", connection.ApiKey);
                request.Headers.Add("anthropic-version", AnthropicVersion);
                break;
            case AiProviderKind.Gemini:
                request.Headers.Add("x-goog-api-key", connection.ApiKey);
                break;
            case AiProviderKind.AzureOpenAI:
                request.Headers.Add("api-key", connection.ApiKey);
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
                break;
        }
    }

    internal static JsonObject AnthropicBody(AiProviderRequest request) => new()
    {
        ["model"] = request.Model,
        ["max_tokens"] = request.MaxOutputTokens,
        ["system"] = request.System,
        ["messages"] = new JsonArray([.. request.Messages.Select(m => (JsonNode)new JsonObject
        {
            ["role"] = m.Role == AiRole.User ? "user" : "assistant",
            ["content"] = m.Text,
        })]),
    };

    internal static JsonObject ChatBody(AiProviderRequest request, AiProviderKind kind)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };
        foreach (var m in request.Messages)
        {
            messages.Add(new JsonObject { ["role"] = m.Role == AiRole.User ? "user" : "assistant", ["content"] = m.Text });
        }

        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages };

        // OpenAI's own APIs use max_completion_tokens (required by reasoning models); servers that
        // copy the API mostly know only the older max_tokens and json_object is not always offered.
        if (kind == AiProviderKind.OpenAICompatible)
        {
            body["max_tokens"] = request.MaxOutputTokens;
        }
        else
        {
            body["max_completion_tokens"] = request.MaxOutputTokens;
            if (request.Json)
            {
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
            }
        }

        return body;
    }

    internal static JsonObject GeminiBody(AiProviderRequest request)
    {
        var config = new JsonObject { ["maxOutputTokens"] = request.MaxOutputTokens };
        if (request.Json)
        {
            config["responseMimeType"] = "application/json";
        }

        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = request.System } } },
            ["contents"] = new JsonArray([.. request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role == AiRole.User ? "user" : "model",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Text } },
            })]),
            ["generationConfig"] = config,
        };
    }

    internal static AiProviderResponse ParseAnthropic(JsonNode json)
    {
        var text = string.Concat((json["content"] as JsonArray ?? [])
            .Where(c => (string?)c?["type"] == "text").Select(c => (string?)c?["text"]));
        return Answer(text, Int(json["usage"]?["input_tokens"]) + Int(json["usage"]?["cache_read_input_tokens"]) + Int(json["usage"]?["cache_creation_input_tokens"]),
            Int(json["usage"]?["output_tokens"]), (string?)json["stop_reason"] == "max_tokens");
    }

    internal static AiProviderResponse ParseChat(JsonNode json)
    {
        var choice = (json["choices"] as JsonArray)?.FirstOrDefault();
        var content = choice?["message"]?["content"];
        var text = content is JsonArray parts
            ? string.Concat(parts.Select(p => (string?)p?["text"]))
            : content is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
        return Answer(text, Int(json["usage"]?["prompt_tokens"]), Int(json["usage"]?["completion_tokens"]), (string?)choice?["finish_reason"] == "length");
    }

    internal static AiProviderResponse ParseGemini(JsonNode json)
    {
        var candidate = (json["candidates"] as JsonArray)?.FirstOrDefault();
        var text = string.Concat((candidate?["content"]?["parts"] as JsonArray ?? [])
            .Where(p => p?["thought"] is null || !(bool)p["thought"]!).Select(p => (string?)p?["text"]));
        if (string.IsNullOrEmpty(text) && ((string?)json["promptFeedback"]?["blockReason"] ?? (string?)candidate?["finishReason"]) is { } reason
            && reason is not "STOP" and not "MAX_TOKENS")
        {
            throw new AiProviderException("provider-rejected", $"The provider did not answer ({reason}).");
        }

        var usage = json["usageMetadata"];
        return Answer(text, Int(usage?["promptTokenCount"]), Int(usage?["candidatesTokenCount"]) + Int(usage?["thoughtsTokenCount"]),
            (string?)candidate?["finishReason"] == "MAX_TOKENS");
    }

    private static AiProviderResponse Answer(string? text, int input, int output, bool truncated) =>
        text is null
            ? throw new AiProviderException("provider-invalid-response", "The provider's answer has no text.")
            : new AiProviderResponse(text, input, output, truncated);

    private static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? (int)Math.Clamp(number, 0, int.MaxValue) : 0;

    private static Uri RequireEndpoint(AiProviderConnection connection) =>
        connection.Endpoint ?? throw new AiProviderException("provider-rejected", "This provider needs an endpoint.");

    // Azure OpenAI: the v1 API (https://<resource>.openai.azure.com/openai/v1/...), or a deployment
    // address with an api-version for resources that still use one.
    private static Uri AzureUrl(AiProviderConnection connection, string? deployment, string path)
    {
        var endpoint = RequireEndpoint(connection);
        if (string.IsNullOrEmpty(connection.ApiVersion) || deployment is null)
        {
            return Combine(endpoint, "openai/v1/" + path);
        }

        return Combine(endpoint, $"openai/deployments/{Uri.EscapeDataString(deployment)}/{path}?api-version={Uri.EscapeDataString(connection.ApiVersion)}");
    }

    internal static Uri Combine(Uri baseAddress, string relative)
    {
        var text = baseAddress.AbsoluteUri;
        return new Uri(text.EndsWith('/') ? text + relative : text + "/" + relative);
    }

    private async Task<string?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > _options.MaxResponseBytes)
                {
                    throw new AiProviderException("provider-invalid-response", "The provider's answer is too large.", (int)response.StatusCode);
                }

                buffer.Write(chunk, 0, count);
            }

            return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    // The provider's own error message helps the administrator (wrong model name, no credit);
    // masked and cut, as it can repeat parts of the request.
    private static string ErrorDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string? message = null;
        try
        {
            var json = JsonNode.Parse(body);
            message = (string?)(json?["error"] is JsonObject error ? error["message"] : json?["error"]) ?? (string?)json?["message"];
        }
        catch (JsonException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return string.IsNullOrWhiteSpace(message) ? string.Empty : " " + Shorten(PersonalDataMasker.MaskText(message.Trim()), 300);
    }

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

    private static string Shorten(string value, int max = 500) => value.Length <= max ? value : value[..max];

    /// <summary>Milliseconds since <paramref name="watch"/> started.</summary>
    internal static int Elapsed(Stopwatch watch) => (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);
}

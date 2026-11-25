using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zelvo.Integrations;

namespace Zelvo.Api.Payments;

/// <summary>How a request body is sent.</summary>
public enum BodyFormat
{
    /// <summary>No body (GET).</summary>
    None,

    /// <summary>A JSON object.</summary>
    Json,

    /// <summary><c>application/x-www-form-urlencoded</c>.</summary>
    Form,
}

/// <summary>HMAC algorithms for signatures.</summary>
public enum SignatureAlgorithm
{
    /// <summary>No signature.</summary>
    None,

    /// <summary>HMAC-SHA256.</summary>
    HmacSha256,

    /// <summary>HMAC-SHA512.</summary>
    HmacSha512,
}

/// <summary>How a signature is written.</summary>
public enum SignatureEncoding
{
    /// <summary>Lower-case hexadecimal.</summary>
    Hex,

    /// <summary>Base64.</summary>
    Base64,
}

/// <summary>A signature over a request Zelvo sends, or a callback the gateway sends.</summary>
public sealed record SignatureSettings
{
    /// <summary>The algorithm; None: no signature.</summary>
    public SignatureAlgorithm Algorithm { get; init; } = SignatureAlgorithm.None;

    /// <summary>The credential holding the key.</summary>
    public string? SecretName { get; init; }

    /// <summary>The header that carries it.</summary>
    public string? Header { get; init; }

    /// <summary>Hex or Base64.</summary>
    public SignatureEncoding Encoding { get; init; } = SignatureEncoding.Hex;

    /// <summary>A text before the signature in the header, for example <c>sha256=</c>.</summary>
    public string? Prefix { get; init; }
}

/// <summary>
/// One call to the gateway: method, address and headers, body fields, and where the answer's
/// values are. Templates use <c>{{placeholders}}</c> (amount, amountMinor, currency, reference,
/// transactionId, description, returnUrl, callbackUrl, merchantId, language, refundAmount,
/// refundAmountMinor, refundReference, secret.NAME).
/// </summary>
public sealed record GatewayOperation
{
    /// <summary>GET, POST, PUT or PATCH.</summary>
    public string Method { get; init; } = "POST";

    /// <summary>The address (HTTPS), a template.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>JSON, form or none.</summary>
    public BodyFormat BodyFormat { get; init; } = BodyFormat.Json;

    /// <summary>Headers, name → template.</summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>Body fields, name (a dot path for nested JSON) → template.</summary>
    public Dictionary<string, string> Body { get; init; } = [];

    /// <summary>Where values are in the answer, name → dot path (for example <c>data.id</c>).</summary>
    public Dictionary<string, string> Response { get; init; } = [];

    /// <summary>A signature over the body Zelvo sends, in a header.</summary>
    public SignatureSettings Signature { get; init; } = new();
}

/// <summary>Which gateway statuses mean paid, failed or cancelled (anything else: still pending).</summary>
public sealed record StatusValues
{
    /// <summary>Paid.</summary>
    public List<string> Paid { get; init; } = [];

    /// <summary>Failed.</summary>
    public List<string> Failed { get; init; } = [];

    /// <summary>Cancelled by the payer.</summary>
    public List<string> Cancelled { get; init; } = [];
}

/// <summary>What the gateway's direct notification looks like.</summary>
public sealed record CallbackSettings
{
    /// <summary>Where the reference is: a JSON dot path or a form field name.</summary>
    public string ReferenceField { get; init; } = "reference";

    /// <summary>How the gateway signs it (HMAC over the raw body).</summary>
    public SignatureSettings Signature { get; init; } = new();
}

/// <summary>The columns of the gateway's settlement file (CSV or Excel).</summary>
public sealed record SettlementColumns
{
    /// <summary>Zelvo's reference.</summary>
    public string Reference { get; init; } = "reference";

    /// <summary>The gateway's transaction id.</summary>
    public string? TransactionId { get; init; }

    /// <summary>Amount.</summary>
    public string Amount { get; init; } = "amount";

    /// <summary>Status.</summary>
    public string? Status { get; init; }

    /// <summary>Whether amounts are in fils (minor units).</summary>
    public bool AmountInMinorUnits { get; init; }
}

/// <summary>
/// Zelvo's one generic payment connector (module 15, decided 2026-11-13: no gateway-specific
/// templates): the customer's administrator maps any gateway that offers a payment API, from the
/// gateway's own documentation.
/// </summary>
public sealed record GatewayConnector
{
    /// <summary>Starts a payment; answers <c>transactionId</c> and <c>redirectUrl</c>.</summary>
    public GatewayOperation Create { get; init; } = new();

    /// <summary>The status API; answers <c>status</c> and, when it gives them, <c>amount</c> and <c>reference</c>.</summary>
    public GatewayOperation Status { get; init; } = new() { Method = "GET", BodyFormat = BodyFormat.None };

    /// <summary>The refund API, when the gateway has one; answers <c>refundId</c> and <c>status</c>.</summary>
    public GatewayOperation? Refund { get; init; }

    /// <summary>Statuses.</summary>
    public StatusValues StatusValues { get; init; } = new();

    /// <summary>Refund statuses that mean done; empty: any successful answer.</summary>
    public List<string> RefundSucceeded { get; init; } = [];

    /// <summary>Whether amounts the status API gives are in fils.</summary>
    public bool StatusAmountInMinorUnits { get; init; }

    /// <summary>The direct notification.</summary>
    public CallbackSettings Callback { get; init; } = new();

    /// <summary>The settlement file, when one is imported for reconciliation.</summary>
    public SettlementColumns? Settlement { get; init; }

    /// <summary>JSON of connectors: camelCase, enums as names.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static GatewayConnector Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new GatewayConnector() : JsonSerializer.Deserialize<GatewayConnector>(json, Json) ?? new GatewayConnector();

    public static string Write(GatewayConnector connector) => JsonSerializer.Serialize(connector, Json);

    /// <summary>Placeholders templates may use (besides <c>secret.NAME</c>).</summary>
    public static IReadOnlyList<string> Placeholders { get; } =
        ["amount", "amountMinor", "currency", "reference", "transactionId", "description", "returnUrl", "callbackUrl", "merchantId", "language", "refundAmount", "refundAmountMinor", "refundReference"];

    /// <summary>Problems with the connector: missing operations, addresses, unknown placeholders and credentials.</summary>
    public List<(string Field, string Code, string Message)> Check(OutboundAddressPolicy policy, IReadOnlyCollection<string> credentials)
    {
        var problems = new List<(string, string, string)>();
        void Operation(GatewayOperation? op, string field, params string[] needs)
        {
            if (op is null)
            {
                return;
            }

            if (!new[] { "GET", "POST", "PUT", "PATCH" }.Contains(op.Method.ToUpperInvariant()))
            {
                problems.Add(($"{field}.method", "method-invalid", "Use GET, POST, PUT or PATCH."));
            }

            // The address itself, with placeholders filled with harmless values.
            var sample = Templates.Render(op.Url, Templates.Sample, url: true);
            if (string.IsNullOrWhiteSpace(op.Url) || policy.CheckUrl(sample, out _) is not null)
            {
                problems.Add(($"{field}.url", "url-invalid", "Use an HTTPS address the installation may reach."));
            }

            foreach (var template in op.Headers.Values.Concat(op.Body.Values).Append(op.Url))
            {
                foreach (var name in Templates.Names(template))
                {
                    if (name.StartsWith("secret.", StringComparison.Ordinal))
                    {
                        if (!credentials.Contains(name[7..]))
                        {
                            problems.Add((field, "credential-unknown", $"No credential is called '{name[7..]}'."));
                        }
                    }
                    else if (!Placeholders.Contains(name))
                    {
                        problems.Add((field, "placeholder-unknown", $"'{{{{{name}}}}}' is not a placeholder."));
                    }
                }
            }

            foreach (var need in needs.Where(n => !op.Response.ContainsKey(n)))
            {
                problems.Add(($"{field}.response", "response-field-missing", $"Say where '{need}' is in the answer."));
            }

            if (op.Signature.Algorithm != SignatureAlgorithm.None && (op.Signature.Header is null || op.Signature.SecretName is null || !credentials.Contains(op.Signature.SecretName)))
            {
                problems.Add(($"{field}.signature", "signature-incomplete", "A signature needs a header and a credential that exists."));
            }
        }

        Operation(Create, "create", "redirectUrl");
        Operation(Status, "status", "status");
        Operation(Refund, "refund");
        if (StatusValues.Paid.Count == 0)
        {
            problems.Add(("statusValues.paid", "paid-status-required", "Say which status means paid."));
        }

        if (Callback.Signature.Algorithm != SignatureAlgorithm.None && (Callback.Signature.Header is null || Callback.Signature.SecretName is null || !credentials.Contains(Callback.Signature.SecretName)))
        {
            problems.Add(("callback.signature", "signature-incomplete", "A signature needs a header and a credential that exists."));
        }

        return problems;
    }
}

/// <summary>Filling templates and reading answers.</summary>
public static partial class Templates
{
    /// <summary>Harmless values for checking addresses.</summary>
    public static IReadOnlyDictionary<string, string> Sample { get; } = GatewayConnector.Placeholders.ToDictionary(p => p, _ => "x", StringComparer.Ordinal);

    /// <summary>The placeholders a template uses.</summary>
    public static IEnumerable<string> Names(string? template) =>
        template is null ? [] : PlaceholderPattern().Matches(template).Select(m => m.Groups[1].Value);

    /// <summary>Fills <paramref name="template"/>; unknown placeholders become empty. In addresses, values are URL-encoded.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values, bool url = false) =>
        PlaceholderPattern().Replace(template, m =>
        {
            var value = values.GetValueOrDefault(m.Groups[1].Value) ?? string.Empty;
            return url ? Uri.EscapeDataString(value) : value;
        });

    /// <summary>The value at a dot path (<c>data.items.0.id</c>) of a JSON answer, as text.</summary>
    public static string? Read(JsonNode? node, string? path)
    {
        if (node is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : node.ToJsonString();
    }

    /// <summary>Sets a dot path in a JSON object; a template that is exactly a numeric placeholder becomes a number.</summary>
    public static void Write(JsonObject target, string path, string template, IReadOnlyDictionary<string, string> values)
    {
        var parts = path.Split('.');
        var node = target;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject child)
            {
                child = [];
                node[part] = child;
            }

            node = child;
        }

        var rendered = Render(template, values);
        var exact = PlaceholderPattern().Match(template);
        var numeric = exact.Success && exact.Value == template.Trim() && exact.Groups[1].Value is "amount" or "amountMinor" or "refundAmount" or "refundAmountMinor";
        node[parts[^1]] = numeric && decimal.TryParse(rendered, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(rendered);
    }

    /// <summary>An HMAC of <paramref name="payload"/> with <paramref name="key"/>.</summary>
    public static string Sign(SignatureAlgorithm algorithm, string key, ReadOnlySpan<byte> payload, SignatureEncoding encoding)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var mac = algorithm == SignatureAlgorithm.HmacSha512 ? HMACSHA512.HashData(keyBytes, payload) : HMACSHA256.HashData(keyBytes, payload);
        return encoding == SignatureEncoding.Base64 ? Convert.ToBase64String(mac) : Convert.ToHexStringLower(mac);
    }

    /// <summary>Whether <paramref name="given"/> is the HMAC of <paramref name="payload"/> (constant time).</summary>
    public static bool Verify(SignatureSettings settings, string key, ReadOnlySpan<byte> payload, string? given)
    {
        if (given is null)
        {
            return false;
        }

        var value = settings.Prefix is { Length: > 0 } prefix && given.StartsWith(prefix, StringComparison.Ordinal) ? given[prefix.Length..] : given;
        var expected = Sign(settings.Algorithm, key, payload, settings.Encoding);
        var a = Encoding.UTF8.GetBytes(settings.Encoding == SignatureEncoding.Hex ? value.Trim().ToLowerInvariant() : value.Trim());
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    [GeneratedRegex(@"\{\{\s*([a-zA-Z][a-zA-Z0-9]*(?:\.[a-zA-Z0-9_-]+)?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}

/// <summary>What a gateway call answered.</summary>
public sealed record GatewayAnswer(int StatusCode, IReadOnlyDictionary<string, string?> Values, string? Error)
{
    /// <summary>A 2xx answer.</summary>
    public bool Ok => StatusCode is >= 200 and < 300 && Error is null;
}

/// <summary>
/// Calls gateways through the outgoing-request policy (<c>Payments:AllowInsecureHttp</c>,
/// <c>Payments:AllowedPrivateNetworks</c>): HTTPS only, no internal addresses unless the operator
/// allows them, checked after DNS. Answers larger than 1 MB are refused. Bodies are never logged.
/// </summary>
public class PaymentGatewayClient : IDisposable
{
    private const int MaxAnswerBytes = 1024 * 1024;
    private readonly HttpClient _http;

    public PaymentGatewayClient(OutboundAddressPolicy policy)
        : this(policy, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, cancellationToken) => policy.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken),
        })
    {
    }

    /// <summary>With another handler (tests).</summary>
    public PaymentGatewayClient(OutboundAddressPolicy policy, HttpMessageHandler handler)
    {
        Policy = policy;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>The address policy.</summary>
    public OutboundAddressPolicy Policy { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Makes <paramref name="operation"/> with <paramref name="values"/> and the credentials.</summary>
    public virtual async Task<GatewayAnswer> SendAsync(
        GatewayOperation operation, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken)
    {
        var all = new Dictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var (name, value) in credentials)
        {
            all["secret." + name] = value;
        }

        var url = Templates.Render(operation.Url, all, url: true);
        if (Policy.CheckUrl(url, out var uri) is { } problem)
        {
            return new GatewayAnswer(0, new Dictionary<string, string?>(), problem);
        }

        using var request = new HttpRequestMessage(new HttpMethod(operation.Method.ToUpperInvariant()), uri);
        byte[] body = [];
        if (operation.BodyFormat == BodyFormat.Json)
        {
            var json = new JsonObject();
            foreach (var (path, template) in operation.Body)
            {
                Templates.Write(json, path, template, all);
            }

            body = Encoding.UTF8.GetBytes(json.ToJsonString());
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        else if (operation.BodyFormat == BodyFormat.Form)
        {
            var form = string.Join("&", operation.Body.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(Templates.Render(f.Value, all))}"));
            body = Encoding.UTF8.GetBytes(form);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
        }

        foreach (var (name, template) in operation.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, Templates.Render(template, all));
        }

        if (operation.Signature.Algorithm != SignatureAlgorithm.None && operation.Signature.SecretName is { } secret && credentials.TryGetValue(secret, out var key) && operation.Signature.Header is { } header)
        {
            request.Headers.TryAddWithoutValidation(header, (operation.Signature.Prefix ?? string.Empty) + Templates.Sign(operation.Signature.Algorithm, key, body, operation.Signature.Encoding));
        }

        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxAnswerBytes)
                {
                    return new GatewayAnswer((int)response.StatusCode, new Dictionary<string, string?>(), "answer-too-large");
                }

                buffer.Write(chunk, 0, read);
            }

            JsonNode? node = null;
            try
            {
                node = buffer.Length == 0 ? null : JsonNode.Parse(buffer.ToArray());
            }
            catch (JsonException)
            {
                return new GatewayAnswer((int)response.StatusCode, new Dictionary<string, string?>(), "answer-not-json");
            }

            var found = operation.Response.ToDictionary(r => r.Key, r => Templates.Read(node, r.Value), StringComparer.Ordinal);
            return new GatewayAnswer((int)response.StatusCode, found, response.IsSuccessStatusCode ? null : $"http-{(int)response.StatusCode}");
        }
        catch (OutboundRequestBlockedException blocked)
        {
            return new GatewayAnswer(0, new Dictionary<string, string?>(), blocked.Code);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new GatewayAnswer(0, new Dictionary<string, string?>(), exception is TaskCanceledException ? "timeout" : "unreachable");
        }
    }
}

/// <summary>Installation options of payments (<c>Payments</c> section).</summary>
public sealed class PaymentOptions
{
    /// <summary>Which gateway addresses may be reached.</summary>
    public OutboundNetworkOptions Network { get; } = new();

    /// <summary>The web app's public address, for the return and callback addresses.</summary>
    public Uri? PublicUrl { get; set; }

    public static PaymentOptions From(IConfiguration configuration)
    {
        var options = new PaymentOptions();
        var section = configuration.GetSection("Payments");
        OutboundNetworkOptions.Read(section, options.Network);
        var url = section["PublicUrl"] ?? configuration["Notifications:WebUrl"];
        options.PublicUrl = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;
        return options;
    }
}

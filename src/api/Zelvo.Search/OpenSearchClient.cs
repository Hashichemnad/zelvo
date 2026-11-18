using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Zelvo.Search;

/// <summary>OpenSearch refused a request or could not be reached.</summary>
public sealed class OpenSearchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OpenSearchException(string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner) => Status = status;

    /// <summary>Creates the exception.</summary>
    public OpenSearchException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public OpenSearchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public OpenSearchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>HTTP status, or null when OpenSearch could not be reached.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>Worth retrying later: unreachable, overloaded or a server error.</summary>
    public bool IsTransient => Status is null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
}

/// <summary>
/// The few OpenSearch REST calls Zelvo needs, over one shared <see cref="HttpClient"/>. Index and
/// alias names are built by <see cref="SearchIndexDefinition"/>, never from user input.
/// </summary>
public sealed class OpenSearchClient : IDisposable
{
    private static readonly MediaTypeHeaderValue Json = new("application/json");
    private static readonly MediaTypeHeaderValue NdJson = new("application/x-ndjson");
    private readonly HttpClient _http;

    /// <summary>Creates a client for <see cref="SearchOptions.OpenSearch"/>.</summary>
    public OpenSearchClient(IOptions<SearchOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = options.Value.OpenSearch,
            Timeout = options.Value.RequestTimeout,
        };
    }

    /// <summary>Indices an alias points at; empty when the alias does not exist.</summary>
    public async Task<IReadOnlyList<string>> GetAliasTargetsAsync(string alias, CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"_alias/{alias}", null, cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound || body is not JsonObject indices ? [] : [.. indices.Select(i => i.Key)];
    }

    /// <summary>Whether an index exists.</summary>
    public async Task<bool> IndexExistsAsync(string index, CancellationToken cancellationToken)
    {
        var (status, _) = await SendAsync(HttpMethod.Head, index, null, cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
        return status == HttpStatusCode.OK;
    }

    /// <summary>Creates an index; does nothing when it exists already.</summary>
    public async Task CreateIndexAsync(string index, JsonObject body, CancellationToken cancellationToken)
    {
        var (status, response) = await SendAsync(HttpMethod.Put, index, body, cancellationToken, HttpStatusCode.BadRequest).ConfigureAwait(false);
        if (status == HttpStatusCode.BadRequest && ErrorType(response) != "resource_already_exists_exception")
        {
            throw new OpenSearchException($"Creating index failed: {ErrorText(response)}", status);
        }
    }

    /// <summary>Deletes an index if it exists.</summary>
    public Task DeleteIndexAsync(string index, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, index, null, cancellationToken, HttpStatusCode.NotFound);

    /// <summary>Applies alias actions atomically.</summary>
    public Task UpdateAliasesAsync(JsonArray actions, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "_aliases", new JsonObject { ["actions"] = actions }, cancellationToken);

    /// <summary>Copies documents keeping their external versions; newer copies in the destination win.</summary>
    public async Task ReindexAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["conflicts"] = "proceed",
            ["source"] = new JsonObject { ["index"] = source },
            ["dest"] = new JsonObject { ["index"] = destination, ["version_type"] = "external" },
        };
        var (_, response) = await SendAsync(HttpMethod.Post, "_reindex?refresh=true&wait_for_completion=true", body, cancellationToken).ConfigureAwait(false);
        if (response?["failures"] is JsonArray { Count: > 0 } failures)
        {
            throw new OpenSearchException($"Reindex from {source} failed: {failures[0]?.ToJsonString()}");
        }
    }

    /// <summary>Runs a bulk request (NDJSON lines) and returns its items in order.</summary>
    public async Task<JsonArray> BulkAsync(IReadOnlyList<JsonNode> lines, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line.ToJsonString()).Append('\n');
        }

        using var content = new StringContent(builder.ToString(), Encoding.UTF8);
        content.Headers.ContentType = NdJson;
        var (_, response) = await SendContentAsync(HttpMethod.Post, "_bulk", content, cancellationToken).ConfigureAwait(false);
        return response?["items"] as JsonArray ?? [];
    }

    /// <summary>Deletes the documents matching a query; returns how many.</summary>
    public async Task<long> DeleteByQueryAsync(string index, JsonObject query, CancellationToken cancellationToken)
    {
        var (status, response) = await SendAsync(HttpMethod.Post, $"{index}/_delete_by_query?conflicts=proceed&refresh=true",
            new JsonObject { ["query"] = query }, cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? 0 : response?["deleted"]?.GetValue<long>() ?? 0;
    }

    /// <summary>Counts documents matching a query; null when the index does not exist.</summary>
    public async Task<long?> CountAsync(string index, JsonObject query, CancellationToken cancellationToken)
    {
        var (status, response) = await SendAsync(HttpMethod.Post, $"{index}/_count",
            new JsonObject { ["query"] = query }, cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? null : response?["count"]?.GetValue<long>();
    }

    /// <summary>Runs a search; null when the index does not exist.</summary>
    public async Task<JsonNode?> SearchAsync(string index, JsonObject body, CancellationToken cancellationToken)
    {
        var (status, response) = await SendAsync(HttpMethod.Post, $"{index}/_search", body, cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? null : response;
    }

    /// <summary>Makes recent writes visible to searches (tests and tools).</summary>
    public Task RefreshAsync(string index, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"{index}/_refresh", null, cancellationToken, HttpStatusCode.NotFound);

    /// <summary>Tokens an index analyser produces for a text (tests and diagnostics).</summary>
    public async Task<IReadOnlyList<string>> AnalyzeAsync(string index, string analyzer, string text, CancellationToken cancellationToken)
    {
        var (_, response) = await SendAsync(HttpMethod.Post, $"{index}/_analyze",
            new JsonObject { ["analyzer"] = analyzer, ["text"] = text }, cancellationToken).ConfigureAwait(false);
        return response?["tokens"] is JsonArray tokens ? [.. tokens.Select(t => t!["token"]!.GetValue<string>())] : [];
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>The type of an OpenSearch error response, for example <c>index_not_found_exception</c>.</summary>
    internal static string? ErrorType(JsonNode? response) =>
        response?["error"] is JsonObject error ? error["type"]?.GetValue<string>() : null;

    /// <summary>A short description of an OpenSearch error, without document content.</summary>
    internal static string ErrorText(JsonNode? response)
    {
        if (response?["error"] is not JsonObject error)
        {
            return "unknown error";
        }

        var type = error["type"]?.GetValue<string>() ?? "error";
        var reason = error["reason"]?.GetValue<string>();
        return reason is null ? type : $"{type}: {reason}";
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, params HttpStatusCode[] allowed)
    {
        HttpContent? content = null;
        if (body is not null)
        {
            content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            content.Headers.ContentType = Json;
        }

        return SendContentAsync(method, path, content, cancellationToken, allowed);
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SendContentAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, params HttpStatusCode[] allowed)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new OpenSearchException("OpenSearch could not be reached.", null, exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenSearchException("OpenSearch did not answer in time.", null, exception);
        }
        finally
        {
            content?.Dispose();
        }

        using (response)
        {
            var text = method == HttpMethod.Head ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? json = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    json = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    json = null;
                }
            }

            if (response.IsSuccessStatusCode || allowed.Contains(response.StatusCode))
            {
                return (response.StatusCode, json);
            }

            throw new OpenSearchException($"OpenSearch answered {(int)response.StatusCode} to {method} {path.Split('?')[0]}: {ErrorText(json)}", response.StatusCode);
        }
    }
}

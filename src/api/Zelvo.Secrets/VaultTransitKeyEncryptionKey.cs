using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace Zelvo.Secrets;

/// <summary>
/// A Transit key in HashiCorp Vault or OpenBao (same API). Data keys are sent to Vault to be
/// wrapped and unwrapped; the Transit key never leaves Vault, and Vault keeps its versions, so a
/// key rotated in Vault still unwraps older data keys. Signs in with a token file or AppRole.
/// </summary>
public sealed class VaultTransitKeyEncryptionKey : IKeyEncryptionKey, IDisposable
{
    private readonly VaultKeyOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _signIn = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires = DateTimeOffset.MaxValue;

    /// <summary>Creates the key; <paramref name="handler"/> replaces the HTTP handler (tests).</summary>
    public VaultTransitKeyEncryptionKey(VaultKeyOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Address is null)
        {
            throw new KeyEncryptionKeyException("Secrets:Vault:Address is required for the Vault key provider.");
        }

        if (!IsPath(options.TransitMount) || !IsPath(options.KeyName) || !IsPath(options.AppRoleMount))
        {
            throw new KeyEncryptionKeyException("Secrets:Vault:TransitMount, KeyName and AppRoleMount are letters, digits, '-', '_', '.' or '/'.");
        }

        if (options.TokenFile is null && (options.RoleIdFile is null || options.SecretIdFile is null))
        {
            throw new KeyEncryptionKeyException("Secrets:Vault needs a TokenFile, or a RoleIdFile and a SecretIdFile (AppRole).");
        }

        _options = options;
        _http = new HttpClient(handler ?? CreateHandler(options))
        {
            BaseAddress = options.Address.AbsoluteUri.EndsWith('/') ? options.Address : new Uri(options.Address.AbsoluteUri + "/"),
            Timeout = options.Timeout,
        };
        Id = $"vault:{options.TransitMount.Trim('/')}/{options.KeyName}";
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public bool CanUnwrap(string kekId) => string.Equals(kekId, Id, StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<byte[]> WrapAsync(byte[] dataKey, CancellationToken cancellationToken)
    {
        var data = await CallAsync("encrypt", new JsonObject { ["plaintext"] = Convert.ToBase64String(dataKey) }, cancellationToken).ConfigureAwait(false);
        var ciphertext = (string?)data["ciphertext"] ?? throw new KeyEncryptionKeyException("Vault returned no ciphertext.");
        return Encoding.ASCII.GetBytes(ciphertext);
    }

    /// <inheritdoc />
    public async Task<byte[]> UnwrapAsync(string kekId, byte[] wrapped, CancellationToken cancellationToken)
    {
        if (!CanUnwrap(kekId))
        {
            throw new KeyEncryptionKeyException("This Vault key did not wrap the data key.");
        }

        var data = await CallAsync("decrypt", new JsonObject { ["ciphertext"] = Encoding.ASCII.GetString(wrapped) }, cancellationToken).ConfigureAwait(false);
        var plaintext = (string?)data["plaintext"] ?? throw new KeyEncryptionKeyException("Vault returned no plaintext.");
        return Convert.FromBase64String(plaintext);
    }

    /// <summary>
    /// Wraps a data key again with the latest version of the Transit key, without Zelvo seeing it
    /// (after the key was rotated in Vault).
    /// </summary>
    public async Task<byte[]> RewrapAsync(byte[] wrapped, CancellationToken cancellationToken)
    {
        var data = await CallAsync("rewrap", new JsonObject { ["ciphertext"] = Encoding.ASCII.GetString(wrapped) }, cancellationToken).ConfigureAwait(false);
        var ciphertext = (string?)data["ciphertext"] ?? throw new KeyEncryptionKeyException("Vault returned no ciphertext.");
        return Encoding.ASCII.GetBytes(ciphertext);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _signIn.Dispose();
    }

    private async Task<JsonNode> CallAsync(string operation, JsonObject body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(forceNew: attempt > 0, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/{_options.TransitMount.Trim('/')}/{operation}/{_options.KeyName}")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("X-Vault-Token", token);
            AddNamespace(request);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new KeyEncryptionKeyException("Vault cannot be reached.", ex);
            }

            using (response)
            {
                // A revoked or rotated token: sign in again once.
                if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Vault's error text never contains key material; keep it short.
                    var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new KeyEncryptionKeyException($"Vault answered {(int)response.StatusCode} to Transit {operation}: {error[..Math.Min(error.Length, 300)]}");
                }

                var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
                return json?["data"] ?? throw new KeyEncryptionKeyException($"Vault returned no data to Transit {operation}.");
            }
        }
    }

    private async Task<string> TokenAsync(bool forceNew, CancellationToken cancellationToken)
    {
        if (!forceNew && _token is not null && DateTimeOffset.UtcNow < _tokenExpires)
        {
            return _token;
        }

        await _signIn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceNew && _token is not null && DateTimeOffset.UtcNow < _tokenExpires)
            {
                return _token;
            }

            if (_options.TokenFile is { } tokenFile)
            {
                _token = ReadFile(tokenFile, "token");
                _tokenExpires = DateTimeOffset.MaxValue;
                return _token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/auth/{_options.AppRoleMount.Trim('/')}/login")
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["role_id"] = ReadFile(_options.RoleIdFile!, "AppRole role id"),
                    ["secret_id"] = ReadFile(_options.SecretIdFile!, "AppRole secret id"),
                }),
            };
            AddNamespace(request);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new KeyEncryptionKeyException($"Vault refused the AppRole sign-in ({(int)response.StatusCode}).");
            }

            var auth = (await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false))?["auth"];
            _token = (string?)auth?["client_token"] ?? throw new KeyEncryptionKeyException("Vault returned no token for the AppRole sign-in.");
            var lease = (int?)auth?["lease_duration"] ?? 0;

            // Sign in again before the token expires (at two thirds of its lease).
            _tokenExpires = lease > 0 ? DateTimeOffset.UtcNow.AddSeconds(lease * 2 / 3.0) : DateTimeOffset.MaxValue;
            return _token;
        }
        finally
        {
            _signIn.Release();
        }
    }

    private void AddNamespace(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_options.Namespace))
        {
            request.Headers.Add("X-Vault-Namespace", _options.Namespace);
        }
    }

    private static string ReadFile(string path, string what)
    {
        try
        {
            var value = File.ReadAllText(path).Trim();
            return value.Length > 0 ? value : throw new KeyEncryptionKeyException($"The Vault {what} file '{path}' is empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new KeyEncryptionKeyException($"The Vault {what} file '{path}' cannot be read.", ex);
        }
    }

    private static SocketsHttpHandler CreateHandler(VaultKeyOptions options)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (options.CaFile is { } caFile)
        {
            var ca = X509CertificateLoader.LoadCertificateFromFile(caFile);
            handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                CustomTrustStore = { ca },
            };
        }

        return handler;
    }

    private static bool IsPath(string value) =>
        value.Length is > 0 and <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/') && !value.Contains("..", StringComparison.Ordinal);
}

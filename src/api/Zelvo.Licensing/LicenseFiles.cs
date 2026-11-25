using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zelvo.Licensing;

/// <summary>A name in English and Arabic.</summary>
internal sealed record BilingualName(
    [property: JsonPropertyName("en")] string? En,
    [property: JsonPropertyName("ar")] string? Ar);

/// <summary>The customer a license is issued to.</summary>
/// <param name="Id">Our customer id (as kept in the customer sheet).</param>
/// <param name="Name">The customer's name.</param>
internal sealed record LicenseCustomer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] BilingualName Name);

/// <summary>
/// The servers an installation runs on (module 12): its installation id and database id, the
/// host's machine id (or the Kubernetes cluster id) and the installation's domain. A license names
/// the fingerprint it was issued for and works only where it matches.
/// </summary>
/// <param name="InstallationId">Random id made once by <c>migrate</c> in the database.</param>
/// <param name="DatabaseId">PostgreSQL's system identifier of the database cluster.</param>
/// <param name="MachineIds">Host machine ids (<c>/etc/machine-id</c>); one per server.</param>
/// <param name="ClusterId">Kubernetes cluster id (Helm installs); machine ids are not checked when set.</param>
/// <param name="Domains">The installation's own domains (its public host).</param>
internal sealed record Fingerprint(
    [property: JsonPropertyName("installationId")] string? InstallationId,
    [property: JsonPropertyName("databaseId")] string? DatabaseId,
    [property: JsonPropertyName("machineIds")] IReadOnlyList<string> MachineIds,
    [property: JsonPropertyName("clusterId")] string? ClusterId,
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains)
{
    /// <summary>
    /// Whether <paramref name="current"/> is an installation this licensed fingerprint accepts,
    /// part by part (null for a part the license does not bind).
    /// </summary>
    public FingerprintMatch Match(Fingerprint current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var installation = Same(InstallationId, current.InstallationId);
        var database = Same(DatabaseId, current.DatabaseId);
        bool? cluster = ClusterId is null ? null : Same(ClusterId, current.ClusterId);
        bool? machine = ClusterId is not null || MachineIds.Count == 0 ? null
            : current.MachineIds.Any(m => MachineIds.Contains(m, StringComparer.OrdinalIgnoreCase));
        bool? domain = Domains.Count == 0 ? null
            : current.Domains.Any(d => Domains.Contains(d, StringComparer.OrdinalIgnoreCase));
        return new FingerprintMatch(installation, database, machine, cluster, domain);
    }

    private static bool Same(string? licensed, string? current) =>
        licensed is not null && current is not null && string.Equals(licensed, current, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which parts of a fingerprint match.</summary>
internal sealed record FingerprintMatch(bool Installation, bool Database, bool? Machine, bool? Cluster, bool? Domain)
{
    public bool All => Installation && Database && Machine != false && Cluster != false && Domain != false;
}

/// <summary>
/// The payload of a license file (<c>type: license</c>) or an upgrade license (<c>type: upgrade</c>).
/// </summary>
internal sealed record LicensePayload
{
    /// <summary><c>license</c> or <c>upgrade</c>.</summary>
    [JsonPropertyName("type")] public required string Type { get; init; }

    /// <summary>The license id (for an upgrade: the license it upgrades).</summary>
    [JsonPropertyName("licenseId")] public required string LicenseId { get; init; }

    /// <summary>An upgrade license's own id.</summary>
    [JsonPropertyName("upgradeId")] public string? UpgradeId { get; init; }

    /// <summary><c>Production</c>, <c>Test</c> or <c>Demo</c> (licenses only).</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }

    /// <summary>The customer (licenses only).</summary>
    [JsonPropertyName("customer")] public LicenseCustomer? Customer { get; init; }

    /// <summary>The hidden customer watermark (licenses only).</summary>
    [JsonPropertyName("watermark")] public string? Watermark { get; init; }

    /// <summary>Licensed modules (licenses only).</summary>
    [JsonPropertyName("modules")] public IReadOnlyList<string> Modules { get; init; } = [];

    /// <summary>Feature version (major.minor) covered: for a license, the version bought; for an upgrade, the version it unlocks.</summary>
    [JsonPropertyName("version")] public required string Version { get; init; }

    /// <summary>When we issued it.</summary>
    [JsonPropertyName("issuedAt")] public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>The activation request it answers.</summary>
    [JsonPropertyName("activationRequestId")] public string? ActivationRequestId { get; init; }

    /// <summary>The servers it is bound to.</summary>
    [JsonPropertyName("fingerprint")] public required Fingerprint Fingerprint { get; init; }

    /// <summary>The version as a <see cref="System.Version"/> (major.minor), or null when malformed.</summary>
    public Version? ParsedVersion() => ParseFeatureVersion(Version);

    /// <summary>The license kind, or null when malformed.</summary>
    public LicenseKind? ParsedKind() => Kind switch
    {
        "Production" => LicenseKind.Production,
        "Test" => LicenseKind.Test,
        "Demo" => LicenseKind.Demo,
        _ => null,
    };

    /// <summary>Reads a payload; null when it is not one.</summary>
    public static LicensePayload? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<LicensePayload>(json);
            if (payload is null || payload.Type is not (SignedFileTypes.License or SignedFileTypes.Upgrade) || string.IsNullOrWhiteSpace(payload.LicenseId)
                || payload.ParsedVersion() is null || payload.Fingerprint is null)
            {
                return null;
            }

            if (payload.Type == SignedFileTypes.License && (payload.ParsedKind() is null || payload.Customer is null))
            {
                return null;
            }

            return payload.Type == SignedFileTypes.Upgrade && string.IsNullOrWhiteSpace(payload.UpgradeId) ? null : payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads <c>major.minor</c> (or a longer version, of which only major and minor count).</summary>
    public static Version? ParseFeatureVersion(string? value) =>
        System.Version.TryParse(value, out var v) && v.Major >= 0 && v.Minor >= 0 ? new Version(v.Major, v.Minor) : null;
}

/// <summary>
/// The activation request file (module 12): made on the customer's servers during setup and sent
/// to us. It is not signed and holds no secrets: an id, the product version and the fingerprint.
/// </summary>
internal sealed record ActivationRequestFile
{
    public const string CurrentFormat = "zelvo-activation-request/1";

    [JsonPropertyName("format")] public string Format { get; init; } = CurrentFormat;

    /// <summary>Random id; the license names it.</summary>
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }

    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The installed product version.</summary>
    [JsonPropertyName("productVersion")] public required string ProductVersion { get; init; }

    /// <summary>Installation profile: Standard, Demo or Test.</summary>
    [JsonPropertyName("profile")] public required string Profile { get; init; }

    /// <summary>The license installed now, if any (for upgrades and server moves).</summary>
    [JsonPropertyName("currentLicenseId")] public string? CurrentLicenseId { get; init; }

    /// <summary>The customer named by the license installed now.</summary>
    [JsonPropertyName("currentCustomerId")] public string? CurrentCustomerId { get; init; }

    [JsonPropertyName("fingerprint")] public required Fingerprint Fingerprint { get; init; }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Indented);

    public static ActivationRequestFile? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<ActivationRequestFile>(json);
            return file is { Format: CurrentFormat } && file.Fingerprint is not null && !string.IsNullOrEmpty(file.RequestId) ? file : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Suggested file name: the installation id's start and the date.</summary>
    public string FileName() =>
        $"zelvo-activation-{(Fingerprint.InstallationId ?? "unknown")[..Math.Min(8, (Fingerprint.InstallationId ?? "unknown").Length)]}-{CreatedAt.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.json";
}

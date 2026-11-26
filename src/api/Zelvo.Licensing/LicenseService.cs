using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Zelvo.Licensing;

/// <summary>
/// Zelvo's license service (module 12): verifies the signed license file kept in the database
/// against our public keys, this installation's fingerprint, its installation profile and the
/// build's watermark; applies upgrade licenses; makes activation requests; and checks our
/// signatures on update packages (module 13).
/// </summary>
/// <remarks>
/// Development builds (without <c>ZELVO_RELEASE</c>) that have no license installed use the
/// development license: every module and version, reported as <see cref="LicenseKind.Development"/>
/// (or <c>Demo</c> / <c>Test</c> for those installation profiles), never in the Production
/// environment. <c>Licensing:DevelopmentLicense=false</c> turns it off to try activation.
/// </remarks>
internal sealed partial class LicenseService : ILicenseService, IInstallationWatermark, IDisposable
{
    internal const string DevelopmentName = "Development build - not for production use";
    internal const string DemoName = "Demo installation - development build";
    internal const string TestName = "Test installation - development build";

    private readonly IInstallationStore _store;
    private readonly ServerFingerprintSource _servers;
    private readonly VendorKeys _keys;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LicenseService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile LicenseStatus _current = LicenseStatus.Invalid(LicenseProblems.NotActivated, "Not verified yet.");
    private string? _lastProblem;

    public LicenseService(
        IInstallationStore store, ServerFingerprintSource servers, VendorKeys keys, IHostEnvironment environment, IConfiguration configuration,
        ILogger<LicenseService> logger, TimeProvider? time = null)
    {
        _store = store;
        _servers = servers;
        _keys = keys;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public LicenseStatus Current => _current;

    public void Dispose() => _lock.Dispose();

    /// <inheritdoc />
    string? IInstallationWatermark.Current => _current.Watermark ?? VendorKeys.BuildWatermark;

    /// <summary>The installation profile (Standard, Demo or Test).</summary>
    internal string Profile => _configuration[InstallationProfile.ConfigurationKey] is { Length: > 0 } p ? p : InstallationProfile.Standard;

    private bool DevelopmentAllowed => !VendorKeys.IsReleaseBuild && !_environment.IsProduction()
        && !string.Equals(_configuration["Licensing:DevelopmentLicense"], "false", StringComparison.OrdinalIgnoreCase);

    public async Task<LicenseStatus> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await ComputeAsync(cancellationToken).ConfigureAwait(false);
            _current = status;
            if (!status.IsValid && status.ProblemCode != _lastProblem)
            {
                LogInvalid(_logger, status.ProblemCode ?? "unknown", status.Reason ?? string.Empty);
            }

            _lastProblem = status.IsValid ? null : status.ProblemCode;
            return status;
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool IsModuleLicensed(string moduleId) => _current is { IsValid: true } current && current.Modules.Contains(moduleId);

    public bool IsVersionLicensed(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var current = _current;
        if (!current.IsValid)
        {
            return false;
        }

        if (current.LicensedVersion is null)
        {
            return true;
        }

        var wanted = new Version(version.Major, Math.Max(0, version.Minor));
        return current.LicensedVersion >= wanted || current.Upgrades.Any(u => u >= wanted);
    }

    public bool VerifyVendorSignature(ReadOnlySpan<byte> data, string keyId, ReadOnlySpan<byte> signature) => _keys.Verify(keyId, data, signature);

    /// <summary>Whether a vendor key id is trusted (to tell an unknown key from a bad signature).</summary>
    internal bool TrustsKey(string keyId) => _keys.Contains(keyId);

    public async Task<ActivationRequest> CreateActivationRequestAsync(CancellationToken cancellationToken = default)
    {
        var fingerprint = await FingerprintAsync(cancellationToken).ConfigureAwait(false);
        string? licenseId = null;
        string? customerId = null;
        try
        {
            var files = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            if (files.LastOrDefault(f => f.FileType == "License") is { } stored && Read(stored.Content, SignedFileTypes.License) is { Payload: { } payload })
            {
                licenseId = payload.LicenseId;
                customerId = payload.Customer?.Id;
            }
        }
        catch (NpgsqlException)
        {
        }

        var request = new ActivationRequestFile
        {
            RequestId = $"AR-{Guid.NewGuid():N}"[..19].ToUpperInvariant(),
            CreatedAt = _time.GetUtcNow(),
            ProductVersion = ProductVersion(),
            Profile = Profile,
            CurrentLicenseId = licenseId,
            CurrentCustomerId = customerId,
            Fingerprint = fingerprint,
        };
        return new ActivationRequest(request.FileName(), request.ToBytes());
    }

    public async Task<LicenseImportResult> ImportLicenseAsync(Stream licenseFile, LicenseImportContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(licenseFile);
        ArgumentNullException.ThrowIfNull(context);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await licenseFile.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > SignedFile.MaxBytes)
            {
                return Refused(LicenseProblems.FormatInvalid, "The file is too large to be a Zelvo license.", null, null);
            }

            buffer.Write(chunk, 0, read);
        }

        var content = buffer.ToArray();
        var file = SignedFile.Parse(content);
        if (file is null || file.Type is not (SignedFileTypes.License or SignedFileTypes.Upgrade))
        {
            return Refused(LicenseProblems.FormatInvalid, "This is not a Zelvo license or upgrade-license file.", null, null);
        }

        var read2 = Read(content, file.Type);
        if (read2.Problem is { } problem)
        {
            return Refused(problem, read2.Reason!, TypeName(file.Type), read2.Payload?.LicenseId);
        }

        var payload = read2.Payload!;
        var fingerprint = await FingerprintAsync(cancellationToken).ConfigureAwait(false);
        var check = CheckBinding(payload, fingerprint);
        if (check is { } binding)
        {
            return Refused(binding.Code, binding.Reason, TypeName(file.Type), payload.LicenseId);
        }

        if (file.Type == SignedFileTypes.Upgrade)
        {
            var current = _current;
            if (!current.IsValid || current.Kind == LicenseKind.Development || current.LicenseId != payload.LicenseId)
            {
                return Refused(LicenseProblems.UpgradeMismatch,
                    $"This upgrade license is for license {payload.LicenseId}; import that license first.", "Upgrade", payload.UpgradeId);
            }
        }

        await _store.AddAsync(TypeName(file.Type), file.Type == SignedFileTypes.Upgrade ? payload.UpgradeId! : payload.LicenseId, content,
            payload.Watermark, context, cancellationToken).ConfigureAwait(false);
        var status = await VerifyAsync(cancellationToken).ConfigureAwait(false);
        return new LicenseImportResult(true, null, null, TypeName(file.Type), payload.UpgradeId ?? payload.LicenseId, status);

        LicenseImportResult Refused(string code, string reason, string? type, string? id) =>
            new(false, code, reason, type, id, _current);
    }

    public async Task<FingerprintReport> GetFingerprintAsync(CancellationToken cancellationToken = default)
    {
        var current = await FingerprintAsync(cancellationToken).ConfigureAwait(false);
        FingerprintMatch? match = null;
        try
        {
            var files = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            if (files.LastOrDefault(f => f.FileType == "License") is { } stored && Read(stored.Content, SignedFileTypes.License) is { Payload: { } payload, Problem: null })
            {
                match = payload.Fingerprint.Match(current);
            }
        }
        catch (NpgsqlException)
        {
        }

        return new FingerprintReport(
        [
            new FingerprintPart("installationId", current.InstallationId, match?.Installation),
            new FingerprintPart("databaseId", current.DatabaseId, match?.Database),
            new FingerprintPart("machineId", current.MachineIds.Count > 0 && current.MachineIds[0] is { } m ? $"{m[..Math.Min(8, m.Length)]}…" : null, match?.Machine),
            new FingerprintPart("clusterId", current.ClusterId, match?.Cluster),
            new FingerprintPart("domain", current.Domains.Count > 0 ? current.Domains[0] : null, match?.Domain),
        ]);
    }

    /// <summary>License files kept (for the license screen); empty when the database is not reachable.</summary>
    internal async Task<IReadOnlyList<StoredLicenseFile>> ListFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return [];
        }
    }

    private async Task<LicenseStatus> ComputeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredLicenseFile> files;
        try
        {
            files = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return DevelopmentAllowed ? DevelopmentStatus()
                : !VendorKeys.IsReleaseBuild && _environment.IsProduction() ? DevelopmentInProduction()
                : LicenseStatus.Invalid(LicenseProblems.StoreUnavailable, "The license could not be read: the database is not reachable.");
        }

        var licenseFile = files.LastOrDefault(f => f.FileType == "License");
        if (licenseFile is null)
        {
            return DevelopmentAllowed ? DevelopmentStatus()
                : !VendorKeys.IsReleaseBuild && _environment.IsProduction() ? DevelopmentInProduction()
                : LicenseStatus.Invalid(LicenseProblems.NotActivated, "Zelvo is not activated: import the license file issued for these servers.");
        }

        var license = Read(licenseFile.Content, SignedFileTypes.License);
        if (license.Problem is { } problem)
        {
            return LicenseStatus.Invalid(problem, license.Reason!);
        }

        var payload = license.Payload!;
        var fingerprint = await FingerprintAsync(cancellationToken).ConfigureAwait(false);
        if (CheckBinding(payload, fingerprint) is { } binding)
        {
            return LicenseStatus.Invalid(binding.Code, binding.Reason);
        }

        var upgrades = new List<Version>();
        foreach (var file in files.Where(f => f.FileType == "Upgrade"))
        {
            var upgrade = Read(file.Content, SignedFileTypes.Upgrade);
            if (upgrade.Problem is { } upgradeProblem)
            {
                // A stored upgrade is checked before it is kept, so a bad one was changed later.
                return LicenseStatus.Invalid(upgradeProblem, $"Upgrade license {file.LicenseId}: {upgrade.Reason}");
            }

            if (upgrade.Payload!.LicenseId == payload.LicenseId && upgrade.Payload.Fingerprint.Match(fingerprint).All)
            {
                upgrades.Add(upgrade.Payload.ParsedVersion()!);
            }
        }

        return new LicenseStatus(true, payload.ParsedKind()!.Value, payload.Customer!.Name.En ?? payload.Customer.Name.Ar ?? payload.Customer.Id,
            payload.LicenseId, payload.Modules.Where(LicensedModules.Phase1.Contains).ToHashSet(StringComparer.Ordinal))
        {
            LicensedToAr = payload.Customer.Name.Ar,
            CustomerId = payload.Customer.Id,
            LicensedVersion = payload.ParsedVersion(),
            Upgrades = [.. upgrades.Distinct().Order()],
            IssuedAt = payload.IssuedAt,
            Watermark = payload.Watermark,
        };
    }

    /// <summary>Checks the signature and reads the payload of a stored or uploaded file.</summary>
    private (LicensePayload? Payload, string? Problem, string? Reason) Read(byte[] content, string expectedType)
    {
        var file = SignedFile.Parse(content);
        if (file is null || file.Type != expectedType)
        {
            return (null, LicenseProblems.FormatInvalid, "The license file is not in Zelvo's format.");
        }

        switch (file.Verify(_keys))
        {
            case SignatureCheck.UnknownKey:
                return (null, LicenseProblems.UnknownKey, $"The license is signed with key '{file.KeyId}', which this build does not trust.");
            case SignatureCheck.Invalid:
                return (null, LicenseProblems.SignatureInvalid, "The license's signature is not valid: the file was changed or was not issued by Zelvo.");
        }

        var payload = LicensePayload.Parse(file.PayloadBytes());
        if (payload is null || payload.Type != expectedType)
        {
            return (null, LicenseProblems.FormatInvalid, "The license's contents are not in Zelvo's format.");
        }

        if (payload.Type == SignedFileTypes.License && !Watermark.IsValid(payload.Watermark))
        {
            return (payload, LicenseProblems.FormatInvalid, "The license carries no valid customer watermark.");
        }

        return (payload, null, null);
    }

    /// <summary>Whether the license fits this installation: profile, build watermark and servers.</summary>
    private (string Code, string Reason)? CheckBinding(LicensePayload payload, Fingerprint fingerprint)
    {
        if (payload.Type == SignedFileTypes.License)
        {
            var expected = Profile.ToUpperInvariant() switch
            {
                "DEMO" => LicenseKind.Demo,
                "TEST" => LicenseKind.Test,
                _ => LicenseKind.Production,
            };
            if (payload.ParsedKind() != expected)
            {
                return (LicenseProblems.KindMismatch, $"This is a {payload.Kind} license, but this is a {Profile} installation.");
            }

            if (VendorKeys.BuildWatermark is { } build && !string.Equals(build, payload.Watermark, StringComparison.Ordinal))
            {
                return (LicenseProblems.WatermarkMismatch, "This build was made for another customer than the license.");
            }
        }

        var match = payload.Fingerprint.Match(fingerprint);
        if (!match.All)
        {
            var parts = new List<string>();
            if (!match.Installation) parts.Add("installation id");
            if (!match.Database) parts.Add("database");
            if (match.Machine == false) parts.Add("server");
            if (match.Cluster == false) parts.Add("cluster");
            if (match.Domain == false) parts.Add("domain");
            return (LicenseProblems.FingerprintMismatch,
                $"The license was issued for other servers ({string.Join(", ", parts)} differ). Send a new activation request to Zelvo.");
        }

        return null;
    }

    private async Task<Fingerprint> FingerprintAsync(CancellationToken cancellationToken)
    {
        (string? InstallationId, string? DatabaseId) identity = (null, null);
        try
        {
            identity = await _store.GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
        }

        return _servers.Collect(identity.InstallationId, identity.DatabaseId);
    }

    private LicenseStatus DevelopmentStatus()
    {
        var profile = Profile;
        if (!string.Equals(profile, InstallationProfile.Standard, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile, InstallationProfile.Demo, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile, InstallationProfile.Test, StringComparison.OrdinalIgnoreCase))
        {
            return LicenseStatus.Invalid(LicenseProblems.FormatInvalid, $"Unknown {InstallationProfile.ConfigurationKey} '{profile}': use Standard, Demo or Test.");
        }

        var demo = string.Equals(profile, InstallationProfile.Demo, StringComparison.OrdinalIgnoreCase);
        var test = string.Equals(profile, InstallationProfile.Test, StringComparison.OrdinalIgnoreCase);
        return new LicenseStatus(
            IsValid: true,
            Kind: demo ? LicenseKind.Demo : test ? LicenseKind.Test : LicenseKind.Development,
            LicensedTo: demo ? DemoName : test ? TestName : DevelopmentName,
            LicenseId: demo ? "dev-demo" : test ? "dev-test" : "dev",
            Modules: LicensedModules.Phase1.ToHashSet(StringComparer.Ordinal));
    }

    private static LicenseStatus DevelopmentInProduction() => LicenseStatus.Invalid(LicenseProblems.DevelopmentInProduction,
        "This is a development build without a license; it cannot run in Production.");

    private static string TypeName(string type) => type == SignedFileTypes.Upgrade ? "Upgrade" : "License";

    /// <summary>The running product version (major.minor.patch).</summary>
    internal static string ProductVersion()
    {
        var version = (Assembly.GetEntryAssembly() ?? typeof(LicenseService).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "License not valid ({Problem}): {Reason}")]
    private static partial void LogInvalid(ILogger logger, string problem, string reason);

}

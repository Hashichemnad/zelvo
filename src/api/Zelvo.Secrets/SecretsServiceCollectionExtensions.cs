using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Zelvo.Secrets;

/// <summary>Registers encryption at rest for stored secrets. Needs <c>AddZelvoData</c>.</summary>
public static class SecretsServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="ISecretProtector"/>, <see cref="DataKeyRewrap"/> and the <see cref="KeyRing"/>
    /// configured in <c>Secrets</c>. The key ring is built on first use, so a host without secrets
    /// configured still starts; the readiness check reports the problem.
    /// </summary>
    public static IServiceCollection AddZelvoSecrets(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<SecretsOptions>().Configure(o => Bind(configuration.GetSection(SecretsOptions.Section), o));
        services.AddMemoryCache();
        services.TryAddSingleton(sp => KeyRing.FromOptions(sp.GetRequiredService<IOptions<SecretsOptions>>().Value));
        services.TryAddScoped<ISecretProtector, SecretProtector>();
        services.TryAddScoped<DataKeyRewrap>();
        return services;
    }

    /// <summary>Reads <c>Secrets</c> into <paramref name="options"/>.</summary>
    public static void Bind(IConfiguration section, SecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(options);
        if (section["Provider"] is { Length: > 0 } provider)
        {
            options.Provider = Enum.TryParse<KeyProviderKind>(provider, ignoreCase: true, out var kind)
                ? kind
                : throw new KeyEncryptionKeyException("Secrets:Provider must be File or Vault.");
        }

        options.DataKeyCacheDuration = Time(section["DataKeyCacheDuration"]) ?? options.DataKeyCacheDuration;

        var file = section.GetSection("File");
        options.File.Path = Text(file["Path"]) ?? options.File.Path;
        foreach (var previous in file.GetSection("PreviousPaths").GetChildren())
        {
            if (Text(previous.Value) is { } path)
            {
                options.File.PreviousPaths.Add(path);
            }
        }

        var vault = section.GetSection("Vault");
        options.Vault.Address = Text(vault["Address"]) is { } address ? new Uri(address) : options.Vault.Address;
        options.Vault.TransitMount = Text(vault["TransitMount"]) ?? options.Vault.TransitMount;
        options.Vault.KeyName = Text(vault["KeyName"]) ?? options.Vault.KeyName;
        options.Vault.Namespace = Text(vault["Namespace"]) ?? options.Vault.Namespace;
        options.Vault.TokenFile = Text(vault["TokenFile"]) ?? options.Vault.TokenFile;
        options.Vault.AppRoleMount = Text(vault["AppRoleMount"]) ?? options.Vault.AppRoleMount;
        options.Vault.RoleIdFile = Text(vault["RoleIdFile"]) ?? options.Vault.RoleIdFile;
        options.Vault.SecretIdFile = Text(vault["SecretIdFile"]) ?? options.Vault.SecretIdFile;
        options.Vault.CaFile = Text(vault["CaFile"]) ?? options.Vault.CaFile;
        options.Vault.Timeout = Time(vault["Timeout"]) ?? options.Vault.Timeout;
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TimeSpan? Time(string? value) =>
        TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var time) ? time : null;
}

namespace Zelvo.Secrets;

/// <summary>Where the key-encryption key comes from.</summary>
public enum KeyProviderKind
{
    /// <summary>A master key file (small installs: a Docker or Kubernetes secret).</summary>
    File,

    /// <summary>A HashiCorp Vault or OpenBao Transit key (medium and large installs).</summary>
    Vault,
}

/// <summary>
/// Encryption at rest for secrets (configuration section <c>Secrets</c>). <see cref="Provider"/>
/// wraps new data keys; every configured provider can unwrap, so an installation can move from a
/// key file to Vault (or to a new key file) and run <c>rewrap-keys</c>.
/// </summary>
public sealed class SecretsOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Secrets";

    /// <summary>The provider that wraps new data keys.</summary>
    public KeyProviderKind Provider { get; set; } = KeyProviderKind.File;

    /// <summary>Master key file settings.</summary>
    public FileKeyOptions File { get; set; } = new();

    /// <summary>Vault or OpenBao Transit settings.</summary>
    public VaultKeyOptions Vault { get; set; } = new();

    /// <summary>How long an unwrapped data key stays in memory.</summary>
    public TimeSpan DataKeyCacheDuration { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Master key files: base64 of 32 random bytes (for example <c>openssl rand -base64 32</c>).</summary>
public sealed class FileKeyOptions
{
    /// <summary>The current master key file; null when no key file is used.</summary>
    public string? Path { get; set; }

    /// <summary>Earlier master key files, kept so data keys they wrapped can still be unwrapped and rewrapped.</summary>
    public IList<string> PreviousPaths { get; } = [];
}

/// <summary>A Vault or OpenBao server with the Transit secrets engine.</summary>
public sealed class VaultKeyOptions
{
    /// <summary>Server address, for example <c>https://vault.example.ae:8200/</c>; null when Vault is not used.</summary>
    public Uri? Address { get; set; }

    /// <summary>Mount path of the Transit engine.</summary>
    public string TransitMount { get; set; } = "transit";

    /// <summary>Name of the Transit key.</summary>
    public string KeyName { get; set; } = "zelvo";

    /// <summary>Vault Enterprise namespace, if any.</summary>
    public string? Namespace { get; set; }

    /// <summary>File holding a Vault token (read again when Vault refuses it, so it can be rotated).</summary>
    public string? TokenFile { get; set; }

    /// <summary>Mount path of the AppRole auth method, used with <see cref="RoleIdFile"/> and <see cref="SecretIdFile"/>.</summary>
    public string AppRoleMount { get; set; } = "approle";

    /// <summary>File holding the AppRole role id.</summary>
    public string? RoleIdFile { get; set; }

    /// <summary>File holding the AppRole secret id.</summary>
    public string? SecretIdFile { get; set; }

    /// <summary>PEM file of the CA that signed the Vault server certificate, when it is not publicly trusted.</summary>
    public string? CaFile { get; set; }

    /// <summary>How long a call to Vault may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

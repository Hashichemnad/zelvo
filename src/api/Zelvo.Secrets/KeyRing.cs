namespace Zelvo.Secrets;

/// <summary>
/// The installation's key-encryption keys: <see cref="Current"/> wraps new data keys; every key
/// in <see cref="All"/> can unwrap the data keys it wrapped (rotation and moving to Vault).
/// </summary>
public sealed class KeyRing : IDisposable
{
    /// <summary>Creates the ring.</summary>
    public KeyRing(IKeyEncryptionKey current, IEnumerable<IKeyEncryptionKey> others)
    {
        ArgumentNullException.ThrowIfNull(current);
        Current = current;
        All = [current, .. (others ?? []).Where(k => !string.Equals(k.Id, current.Id, StringComparison.Ordinal))];
    }

    /// <summary>The key that wraps new data keys.</summary>
    public IKeyEncryptionKey Current { get; }

    /// <summary>Every configured key, current first.</summary>
    public IReadOnlyList<IKeyEncryptionKey> All { get; }

    /// <summary>The key that wrapped data keys with <paramref name="kekId"/>.</summary>
    public IKeyEncryptionKey For(string kekId) =>
        All.FirstOrDefault(k => k.CanUnwrap(kekId))
        ?? throw new KeyEncryptionKeyException($"No configured key-encryption key can unwrap data keys of '{kekId}'. Configure that key (Secrets:File:PreviousPaths or Secrets:Vault).");

    /// <summary>Builds the ring from configuration.</summary>
    public static KeyRing FromOptions(SecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var keys = new List<IKeyEncryptionKey>();
        IKeyEncryptionKey? file = options.File.Path is { Length: > 0 } path ? FileKeyEncryptionKey.Load(path) : null;
        IKeyEncryptionKey? vault = options.Vault.Address is not null ? new VaultTransitKeyEncryptionKey(options.Vault) : null;
        if (file is not null)
        {
            keys.Add(file);
        }

        if (vault is not null)
        {
            keys.Add(vault);
        }

        keys.AddRange(options.File.PreviousPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(FileKeyEncryptionKey.Load));

        var current = options.Provider switch
        {
            KeyProviderKind.File => file ?? throw new KeyEncryptionKeyException("Secrets:File:Path is required: the master key file that protects stored secrets."),
            KeyProviderKind.Vault => vault ?? throw new KeyEncryptionKeyException("Secrets:Vault:Address is required for the Vault key provider."),
            _ => throw new KeyEncryptionKeyException("Secrets:Provider must be File or Vault."),
        };
        return new KeyRing(current, keys);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var key in All.OfType<IDisposable>())
        {
            key.Dispose();
        }
    }
}

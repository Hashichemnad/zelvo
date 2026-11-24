using System.Text.Json;
using Zelvo.Data.Channels;
using Zelvo.Secrets;

namespace Zelvo.Channels;

/// <summary>Why Zelvo could not use a mailbox; the code is shown in Settings and the log.</summary>
public sealed class MailboxException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Stable code, for example <c>sign-in-failed</c>, <c>unreachable</c>, <c>address-not-allowed</c>.</summary>
    public string Code { get; } = code;

    /// <summary>Text for the mailbox's last error: code and message.</summary>
    public string Summary => $"{Code}: {Message}";
}

/// <summary>
/// The decrypted credential of a mailbox. For <see cref="MailboxAuth.OAuthSignIn"/> the stored value
/// is a JSON object with the client secret and, once an administrator signed in, the refresh token.
/// </summary>
/// <param name="Secret">Password, client secret or service-account key (JSON).</param>
/// <param name="RefreshToken">OAuth sign-in: the refresh token.</param>
public sealed record MailboxCredential(string Secret, string? RefreshToken = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Purpose the credential is encrypted for (binds the ciphertext to the mailbox).</summary>
    public static string Purpose(Guid mailboxId) => $"channels.mailbox:{mailboxId:N}.credential";

    /// <summary>The stored form for <paramref name="auth"/>.</summary>
    public string Serialize(MailboxAuth auth) =>
        auth == MailboxAuth.OAuthSignIn ? JsonSerializer.Serialize(new Stored(Secret, RefreshToken), Json) : Secret;

    /// <summary>Reads the stored form.</summary>
    public static MailboxCredential Deserialize(MailboxAuth auth, string value)
    {
        if (auth != MailboxAuth.OAuthSignIn)
        {
            return new(value);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(value, Json);
            return new(stored?.ClientSecret ?? string.Empty, stored?.RefreshToken);
        }
        catch (JsonException)
        {
            return new(string.Empty);
        }
    }

    /// <summary>Decrypts the mailbox's credential.</summary>
    public static async Task<MailboxCredential> ReadAsync(Mailbox mailbox, ISecretProtector secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(secrets);
        if (mailbox.CredentialCiphertext is null)
        {
            throw new MailboxException("credential-missing", "Enter the mailbox's credential in Settings → Mailboxes.");
        }

        try
        {
            var value = await secrets.UnprotectAsync(Purpose(mailbox.Id), mailbox.CredentialCiphertext, cancellationToken).ConfigureAwait(false);
            return Deserialize(mailbox.Auth, value);
        }
        catch (SecretDecryptionException ex)
        {
            throw new MailboxException("credential-unreadable", "The stored credential cannot be read; enter it again.", ex);
        }
    }

    /// <summary>Encrypts <paramref name="credential"/> for <paramref name="mailbox"/>.</summary>
    public static Task<string> ProtectAsync(Mailbox mailbox, MailboxCredential credential, ISecretProtector secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(secrets);
        return secrets.ProtectAsync(Purpose(mailbox.Id), credential.Serialize(mailbox.Auth), cancellationToken);
    }

    private sealed record Stored(string? ClientSecret, string? RefreshToken);
}

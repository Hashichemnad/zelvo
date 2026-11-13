using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Settings;

/// <summary>
/// One tenant setting, stored as JSON under a key such as <c>localization.defaultLanguage</c>.
/// What a key means, its type, default and who may change it are defined in code by the module
/// that owns it; a key without a row has its default value. Changes are audited.
/// </summary>
public sealed class TenantSetting : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 100;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Setting key: lower camel case segments separated by dots.</summary>
    public required string Key { get; init; }

    /// <summary>The value as JSON.</summary>
    public required string Value { get; set; }

    /// <summary>When the setting was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

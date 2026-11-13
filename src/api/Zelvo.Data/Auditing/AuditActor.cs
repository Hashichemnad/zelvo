namespace Zelvo.Data.Auditing;

/// <summary>Who is acting and from where, for the audit log of the current scope.</summary>
public interface IAuditContext
{
    /// <summary>Kind of actor.</summary>
    AuditActorType ActorType { get; }

    /// <summary>Actor id, when known.</summary>
    string? ActorId { get; }

    /// <summary>Actor display name, when known.</summary>
    string? ActorName { get; }

    /// <summary>Request details recorded with every entry (correlation id, client address, user agent).</summary>
    IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>Settable <see cref="IAuditContext"/>; one per request or job. Defaults to the system actor.</summary>
public sealed class AuditContext : IAuditContext
{
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public AuditActorType ActorType { get; private set; } = AuditActorType.System;

    /// <inheritdoc />
    public string? ActorId { get; private set; }

    /// <inheritdoc />
    public string? ActorName { get; private set; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    /// <summary>Sets the actor.</summary>
    public void SetActor(AuditActorType type, string? id, string? name)
    {
        ActorType = type;
        ActorId = id;
        ActorName = name;
    }

    /// <summary>Adds or replaces a metadata value; blank values are ignored.</summary>
    public void SetMetadata(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _metadata[key] = value.Length > AuditEvent.TextMaxLength ? value[..AuditEvent.TextMaxLength] : value;
        }
    }
}

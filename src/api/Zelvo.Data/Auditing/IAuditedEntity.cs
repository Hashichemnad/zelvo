namespace Zelvo.Data.Auditing;

/// <summary>
/// A record whose creation, every field change and deletion is written to the audit log
/// automatically when it is saved, in the same transaction.
/// </summary>
public interface IAuditedEntity;

/// <summary>
/// Marks a property holding personal or secret data. The audit log records that it changed but
/// masks its old and new values.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveDataAttribute : Attribute;

/// <summary>
/// Marks long text (for example an article body) whose old and new values would bloat the audit
/// log: the log records that it changed and its length, not the text. The full text is kept
/// elsewhere (for articles, in every version).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditLengthOnlyAttribute : Attribute;

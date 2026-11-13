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

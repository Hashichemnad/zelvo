using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Apps;

/// <summary>
/// An app of the low-code app builder (module 20, task K3; product owner decision 20 of
/// 2026-11-26): custom record types (K2) with their forms and workflow rules, data lists and roles
/// grouped under one bilingual name and icon, with its own list views, menu entries and portal
/// entries. Apps move between installations as JSON packages (<c>zelvo-app/1</c>); an imported
/// package becomes an app only when an administrator approves it. No custom code (module 28).
/// </summary>
public sealed class App : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 40;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Key in addresses and packages (<c>hr</c>); never changes.</summary>
    public required string Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What it is for.</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>One of Zelvo's icons (the service catalog's set).</summary>
    public string Icon { get; set; } = "service";

    /// <summary>Active apps show their menu and portal entries and their record types take new records.</summary>
    public bool Active { get; set; } = true;

    /// <summary>List views, menu entries and portal entries (JSON object, checked by the API).</summary>
    public string Definition { get; set; } = "{}";

    /// <summary>
    /// What switching the app off switched off (JSON: <c>{ recordTypes: [ids], rules: [ids] }</c>),
    /// so switching it on again restores exactly those.
    /// </summary>
    public string Suspended { get; set; } = "{}";

    /// <summary>The approved import it came from, if any.</summary>
    public Guid? ImportId { get; set; }

    /// <summary>Who made it (or approved its import).</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// A simple data list (task K3): an administrator-defined table, for example vehicles or rooms,
/// used as the options of dropdown and radio fields of forms (C1).
/// </summary>
public sealed class DataList : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 40;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Most extra columns.</summary>
    public const int MaxColumns = 10;

    /// <summary>Most items.</summary>
    public const int MaxItems = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Key that forms refer to (<c>vehicles</c>); never changes.</summary>
    public required string Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What it holds.</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>Extra columns (JSON array of <c>{ key, label: { en, ar } }</c>), for example a vehicle's plate number.</summary>
    public string Columns { get; set; } = "[]";

    /// <summary>The app it belongs to, if any.</summary>
    public Guid? AppId { get; set; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>An item of a data list: the value stored in answers, its bilingual label and its columns' values.</summary>
public sealed class DataListItem : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/> (as option values of forms).</summary>
    public const int KeyMaxLength = 100;

    /// <summary>Maximum length of each language of <see cref="Label"/>.</summary>
    public const int LabelMaxLength = 200;

    /// <summary>Maximum length of a column's value.</summary>
    public const int ValueMaxLength = 500;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The list.</summary>
    public Guid DataListId { get; init; }

    /// <summary>The value stored in answers; stable when the label changes.</summary>
    public required string Key { get; init; }

    /// <summary>Label in English and Arabic.</summary>
    public required LocalizedText Label { get; set; }

    /// <summary>The columns' values (JSON object by column key).</summary>
    public string Values { get; set; } = "{}";

    /// <summary>Inactive items are no longer offered; answers that chose them keep them.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Order in the list.</summary>
    public int Position { get; set; }
}

/// <summary>Where an imported app package is in its review.</summary>
public enum AppImportStatus
{
    /// <summary>Checked and waiting for an administrator; nothing of it is active.</summary>
    Pending,

    /// <summary>Approved: its contents were added (or replaced what the administrator chose) and the app is active.</summary>
    Approved,

    /// <summary>Discarded without changing anything.</summary>
    Discarded,
}

/// <summary>
/// An app package imported into the installation (task K3, decided 2026-11-26): kept as it came,
/// validated and previewed, and inactive until an administrator approves it.
/// </summary>
public sealed class AppImport : ITenantOwned, IAuditedEntity
{
    /// <summary>Largest package in bytes.</summary>
    public const int MaxPackageBytes = 5 * 1024 * 1024;

    /// <summary>Maximum length of <see cref="FileName"/>.</summary>
    public const int FileNameMaxLength = 200;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The uploaded file's name.</summary>
    public string? FileName { get; init; }

    /// <summary>SHA-256 of the package as uploaded (hex).</summary>
    public required string Sha256 { get; init; }

    /// <summary>The package (JSON).</summary>
    [AuditLengthOnly]
    public required string Package { get; init; }

    /// <summary>The app's key in the package.</summary>
    public required string AppKey { get; init; }

    /// <summary>The app's name in the package.</summary>
    public required LocalizedText AppName { get; init; }

    /// <summary>Where it is in its review.</summary>
    public AppImportStatus Status { get; set; } = AppImportStatus.Pending;

    /// <summary>Who imported it.</summary>
    public Guid? ImportedById { get; init; }

    /// <summary>When it was imported.</summary>
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Who approved or discarded it.</summary>
    public Guid? DecidedById { get; set; }

    /// <summary>When it was approved or discarded.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The administrator's choice for each item that already exists (JSON object, <c>keep</c> or <c>replace</c>).</summary>
    public string Choices { get; set; } = "{}";

    /// <summary>What the approval added and changed (JSON).</summary>
    public string Result { get; set; } = "{}";

    /// <summary>The app it became.</summary>
    public Guid? AppId { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

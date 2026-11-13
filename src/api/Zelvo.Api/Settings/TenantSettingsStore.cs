using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Settings;

namespace Zelvo.Api.Settings;

/// <summary>A tenant setting: its key, type, default and the permission needed to change it.</summary>
internal abstract class SettingDefinition(string key, string editPermission)
{
    /// <summary>Key, for example <c>localization.defaultLanguage</c>.</summary>
    public string Key { get; } = key;

    /// <summary>Permission needed to change the setting.</summary>
    public string EditPermission { get; } = editPermission;
}

/// <summary>A tenant setting holding a <typeparamref name="T"/>.</summary>
internal sealed class SettingDefinition<T>(string key, T defaultValue, string editPermission, Func<T, bool>? isValid = null)
    : SettingDefinition(key, editPermission)
{
    /// <summary>Value used while the tenant has not changed the setting.</summary>
    public T Default { get; } = defaultValue;

    /// <summary>Whether <paramref name="value"/> is allowed. Stored values that are not fall back to <see cref="Default"/>.</summary>
    public bool IsValid(T value) => value is not null && (isValid?.Invoke(value) ?? true);
}

/// <summary>
/// Reads and changes the current tenant's settings (row-level security limits the rows to it).
/// A setting without a row has its default. Changes are saved with the request's
/// <c>SaveChangesAsync</c> and audited like any other record.
/// </summary>
internal sealed class TenantSettingsStore(ZelvoDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private Dictionary<string, TenantSetting>? _rows;

    /// <summary>The setting's value for the current tenant.</summary>
    public async Task<T> GetAsync<T>(SettingDefinition<T> setting, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(cancellationToken).ConfigureAwait(false);
        if (!rows.TryGetValue(setting.Key, out var row))
        {
            return setting.Default;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(row.Value, Json);
            return value is not null && setting.IsValid(value) ? value : setting.Default;
        }
        catch (JsonException)
        {
            return setting.Default;
        }
    }

    /// <summary>When any of <paramref name="settings"/> was last changed; <see langword="null"/> if never.</summary>
    public async Task<DateTimeOffset?> LastChangedAsync(IEnumerable<SettingDefinition> settings, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(cancellationToken).ConfigureAwait(false);
        return settings.Select(s => rows.GetValueOrDefault(s.Key)?.UpdatedAt).Max();
    }

    /// <summary>Stages a new value; saved by the caller's <c>SaveChangesAsync</c>. Unchanged values write nothing.</summary>
    public async Task SetAsync<T>(SettingDefinition<T> setting, T value, CancellationToken cancellationToken)
    {
        if (!setting.IsValid(value))
        {
            throw new ArgumentException($"Not a valid value for {setting.Key}.", nameof(value));
        }

        var json = JsonSerializer.Serialize(value, Json);
        var rows = await RowsAsync(cancellationToken).ConfigureAwait(false);
        if (rows.TryGetValue(setting.Key, out var row))
        {
            if (JsonEquals(row.Value, json))
            {
                return;
            }

            row.Value = json;
            row.UpdatedAt = Now();
            return;
        }

        row = new TenantSetting { Key = setting.Key, Value = json, UpdatedAt = Now() };
        db.TenantSettings.Add(row);
        rows[setting.Key] = row;
    }

    private async Task<Dictionary<string, TenantSetting>> RowsAsync(CancellationToken cancellationToken) =>
        _rows ??= await db.TenantSettings.ToDictionaryAsync(s => s.Key, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

    // PostgreSQL keeps microseconds; the response shows what is stored.
    private static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % 10));
    }

    // PostgreSQL normalises jsonb, so compare parsed values rather than text.
    private static bool JsonEquals(string a, string b)
    {
        using var left = JsonDocument.Parse(a);
        using var right = JsonDocument.Parse(b);
        return JsonElement.DeepEquals(left.RootElement, right.RootElement);
    }
}

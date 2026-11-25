using Zelvo.Data;
using Zelvo.Tickets;

namespace Zelvo.Notifications;

/// <summary>What an administrator chose for one event.</summary>
/// <param name="Enabled">Whether the event notifies anyone (in-app and email).</param>
/// <param name="Email">Whether it also sends email (people may still turn email off when the event allows it).</param>
public sealed record EventSetting(bool Enabled, bool Email, bool Messaging = true);

/// <summary>
/// Settings → Notifications, stored in <c>tenant_settings</c> under <see cref="EventsKey"/>: each
/// event on or off, with or without email. Events not listed are on, with email.
/// </summary>
public static class NotificationSettings
{
    /// <summary>Setting key.</summary>
    public const string EventsKey = "notifications.events";

    /// <summary>Every event on, with email.</summary>
    public static IReadOnlyDictionary<string, EventSetting> Default { get; } = new Dictionary<string, EventSetting>(StringComparer.Ordinal);

    /// <summary>Only known events.</summary>
    public static bool IsValid(IReadOnlyDictionary<string, EventSetting>? settings) =>
        settings is not null && settings.Keys.All(k => NotificationEvents.Find(k) is not null) && settings.Values.All(v => v is not null);

    /// <summary>The setting of <paramref name="eventType"/> in <paramref name="settings"/>.</summary>
    public static EventSetting For(IReadOnlyDictionary<string, EventSetting> settings, string eventType)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.TryGetValue(eventType, out var setting) ? setting : new EventSetting(true, true);
    }

    /// <summary>The tenant's settings, read straight from <c>tenant_settings</c> (the API and the worker).</summary>
    public static Task<IReadOnlyDictionary<string, EventSetting>> ReadAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TicketSettingKeys.ReadAsync(db, EventsKey, Default, IsValid, cancellationToken);
}

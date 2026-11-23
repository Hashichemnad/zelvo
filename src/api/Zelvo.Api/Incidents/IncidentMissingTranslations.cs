using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Incidents;

/// <summary>
/// Reports names in the incident settings that miss a language (LANG-5): impact, urgency and
/// priority names, and on-hold reasons. The built-in defaults have both languages.
/// </summary>
internal sealed class IncidentMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.TenantSettings.AsNoTracking()
            .Where(s => s.Key == TicketSettingKeys.PriorityMatrix || s.Key == TicketSettingKeys.OnHoldReasons)
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var missing = new List<MissingTranslation>();
        if (rows.TryGetValue(TicketSettingKeys.PriorityMatrix, out var matrixJson) && Read<PriorityMatrix>(matrixJson) is { } matrix && matrix.IsValid())
        {
            Add(missing, "priorityMatrix", "impacts", matrix.Impacts);
            Add(missing, "priorityMatrix", "urgencies", matrix.Urgencies);
            Add(missing, "priorityMatrix", "priorities", matrix.Priorities);
        }

        if (rows.TryGetValue(TicketSettingKeys.OnHoldReasons, out var reasonsJson) && Read<List<OnHoldReason>>(reasonsJson) is { } reasons && OnHoldReason.AreValid(reasons))
        {
            foreach (var reason in reasons.Where(r => !r.Name.ToText().IsComplete))
            {
                missing.Add(new MissingTranslation("incident_settings", "onHoldReasons", reason.Key, reason.Name.ToText().MissingLanguages));
            }
        }

        return missing;
    }

    private static void Add(List<MissingTranslation> missing, string id, string list, IReadOnlyList<SettingText> names)
    {
        for (var i = 0; i < names.Count; i++)
        {
            var text = names[i].ToText();
            if (!text.IsComplete)
            {
                missing.Add(new MissingTranslation("incident_settings", id, $"{list}.{i + 1}", text.MissingLanguages));
            }
        }
    }

    private static T? Read<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, TicketSettingKeys.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

import { useTranslations } from "next-intl";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, levelName, reasonName, ticketStates, type HistoryEntry, type IncidentSettings, type Locale, type TicketState } from "@/lib/incidents";

/** The incident's history from the audit log: who changed what, with old and new values. */
export function IncidentHistory({ entries, settings, display }: { entries: HistoryEntry[]; settings: IncidentSettings | null; display: DateDisplayOptions }) {
  const t = useTranslations("incidents.history");
  const tStates = useTranslations("incidents.states");
  const locale = display.locale as Locale;
  const matrix = settings?.priorityMatrix;

  const show = (field: string, value: unknown): string => {
    if (value === null || value === undefined || value === "") return t("empty");
    if (field === "state" && ticketStates.includes(value as TicketState)) return tStates(value as TicketState);
    if (field === "priority" && matrix) return levelName(matrix.priorities, Number(value), locale);
    if (field === "impact" && matrix) return levelName(matrix.impacts, Number(value), locale);
    if (field === "urgency" && matrix) return levelName(matrix.urgencies, Number(value), locale);
    if (field === "onHoldReason") return reasonName(settings?.onHoldReasons ?? [], String(value), locale);
    if (typeof value === "boolean") return value ? "✓" : "✗";
    // Groups, categories, locations and rules may come back as bilingual names (task B2).
    if (typeof value === "object" && value !== null && ("en" in value || "ar" in value)) return inLanguage(value as { en: string | null; ar: string | null }, locale).value || t("empty");
    const text = String(value);
    return text.length > 200 ? `${text.slice(0, 200)}…` : text;
  };

  const actionOf = (entry: HistoryEntry): string => {
    const fileName = String(entry.details?.fileName ?? "");
    const key = entry.action.replaceAll(".", "_");
    if (t.has(`actions.${key}`)) return t(`actions.${key}`, { fileName });
    return entry.category === "Create" ? t("actions.created") : t("actions.changed");
  };

  // Field-by-field changes of the ticket and its incident details; noise such as timestamps is left out.
  const hidden = new Set(["id", "updatedAt", "createdAt", "resolvedAt", "closedAt", "majorDeclaredAt", "majorDeclaredById", "number", "sequence", "type", "createdById", "version"]);

  return (
    <section aria-labelledby="incident-history" className="space-y-3">
      <h2 id="incident-history" className="text-xl font-bold">
        {t("heading")}
      </h2>
      {entries.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <ol className="space-y-2">
          {entries.map((entry) => {
            const changes = Object.entries(entry.changes ?? {}).filter(([field]) => !hidden.has(field));
            if (entry.category !== "Create" && changes.length === 0 && !t.has(`actions.${entry.action.replaceAll(".", "_")}`)) return null;
            return (
              <li key={entry.sequence} className="rounded-lg border border-line p-3 text-sm">
                <p>
                  <span className="font-semibold">{entry.actorType === "System" ? (entry.actorName ?? t("system")) : (entry.actorName ?? "—")}</span> · {actionOf(entry)} ·{" "}
                  <span className="text-ink-muted">{formatDateTime(new Date(entry.occurredAt), display)}</span>
                </p>
                {entry.category !== "Create" && changes.length > 0 && (
                  <ul className="mt-1 list-disc ps-5">
                    {changes.map(([field, change]) => (
                      <li key={field}>
                        <span dir="auto">
                          {t("change", { field: t.has(`fields.${field}`) ? t(`fields.${field}`) : field, old: show(field, change.old), new: show(field, change.new) })}
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            );
          })}
        </ol>
      )}
    </section>
  );
}

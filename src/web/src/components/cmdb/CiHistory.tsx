import { useTranslations } from "next-intl";
import { ciStatuses, type CiStatus } from "@/lib/cmdb";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type HistoryEntry, type Locale } from "@/lib/incidents";

/** Fields of the audit log's changes that say nothing to people. */
const hidden = new Set(["id", "updatedAt", "createdAt", "number", "sequence", "version", "tenantId"]);

/**
 * A configuration item's history from the audit log (task G1): field changes, relationships added
 * and removed, and tickets it was put on or taken off.
 */
export function CiHistory({ entries, display }: { entries: HistoryEntry[]; display: DateDisplayOptions }) {
  const t = useTranslations("cmdb.history");
  const tStatuses = useTranslations("cmdb.statuses");
  const locale = display.locale as Locale;

  const show = (field: string, value: unknown): string => {
    if (value === null || value === undefined || value === "") return t("empty");
    if (field === "status" && ciStatuses.includes(value as CiStatus)) return tStatuses(value as CiStatus);
    if (typeof value === "boolean") return value ? "✓" : "✗";
    if (typeof value === "object" && ("en" in (value as object) || "ar" in (value as object))) {
      return inLanguage(value as { en: string | null; ar: string | null }, locale).value || t("empty");
    }
    const text = typeof value === "object" ? JSON.stringify(value) : String(value);
    return text.length > 200 ? `${text.slice(0, 200)}…` : text;
  };

  const actionOf = (entry: HistoryEntry): string => {
    const key = entry.action.replaceAll(".", "_").replaceAll("-", "_");
    const d = entry.details ?? {};
    const params = {
      relation: String((locale === "ar" ? d.typeNameAr : undefined) ?? d.typeName ?? d.type ?? ""),
      source: String(d.source ?? ""),
      target: String(d.target ?? ""),
      ticket: String(d.ticket ?? ""),
    };
    if (t.has(`actions.${key}`)) return t(`actions.${key}`, params);
    return entry.category === "Create" ? t("actions.created") : entry.category === "Delete" ? t("actions.deleted") : t("actions.changed");
  };

  return (
    <section aria-labelledby="ci-history" className="space-y-3">
      <h2 id="ci-history" className="text-xl font-bold">{t("heading")}</h2>
      {entries.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <ol className="space-y-2">
          {entries.map((entry) => {
            const changes = Object.entries(entry.changes ?? {}).filter(([field]) => !hidden.has(field));
            return (
              <li key={entry.sequence} className="rounded-lg border border-line p-3 text-sm">
                <p>
                  <span className="font-semibold">{entry.actorType === "System" ? (entry.actorName ?? t("system")) : (entry.actorName ?? t("unknownPerson"))}</span> · {actionOf(entry)} ·{" "}
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

import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import type { CiAttribute, CiDiscovery } from "@/lib/cmdb";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { textIn } from "@/lib/cmdb";
import type { Locale } from "@/lib/incidents";

/**
 * Where an item's data comes from (task G2): the item it was merged into, whether its sources
 * still report it, each source's record, who set each value last (a person or a source) and the
 * software the sources found on it.
 */
export function CiDiscoveryPanel({ discovery, attributes, display }: { discovery: CiDiscovery; attributes: CiAttribute[]; display: DateDisplayOptions }) {
  const t = useTranslations("cmdb.itemDiscovery");
  const tKinds = useTranslations("cmdb.discovery.kinds");
  const locale = display.locale as Locale;
  const labelOf = (key: string) => {
    if (key === "name") return t("nameValue");
    const attribute = attributes.find((a) => a.field.key === key);
    const label = attribute?.field.label;
    return label ? textIn({ en: label.en ?? null, ar: label.ar ?? null }, locale) || key : key;
  };
  const setters = Object.entries(discovery.valueSetters);

  return (
    <section aria-labelledby="ci-discovery" className="space-y-4" data-testid="ci-discovery">
      <h2 id="ci-discovery" className="text-xl font-bold">{t("heading")}</h2>
      {discovery.mergedInto && (
        <p role="note" className="rounded-md border border-line p-3 font-semibold">
          {t("mergedInto")}{" "}
          <Link href={`/cmdb/${discovery.mergedInto.id}`} className="font-mono text-brand underline-offset-4 hover:underline" dir="ltr">{discovery.mergedInto.number}</Link>{" "}
          <span dir="auto">{discovery.mergedInto.name}</span>
        </p>
      )}
      {discovery.missingSince && (
        <p role="note" className="rounded-md border border-bad p-3 font-semibold text-bad">
          {t("missingSince", { date: formatDateTime(new Date(discovery.missingSince), display) })}
        </p>
      )}
      {discovery.records.length === 0 ? (
        <p>{t("noSources")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="text-start font-semibold">{t("sourcesCaption")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("source")}</th>
                <th scope="col" className={cellClass}>{t("externalId")}</th>
                <th scope="col" className={cellClass}>{t("lastSeen")}</th>
              </tr>
            </thead>
            <tbody>
              {discovery.records.map((r) => (
                <tr key={`${r.source?.id}-${r.externalId}`}>
                  <td className={cellClass}>
                    <span dir="auto">{r.source?.name ?? "—"}</span>
                    {r.source && <span className="block text-sm text-ink-muted">{tKinds(r.source.kind)}</span>}
                  </td>
                  <td className={cellClass}><code dir="ltr" className="break-all">{r.externalId}</code></td>
                  <td className={cellClass}>
                    {formatDateTime(new Date(r.lastSeenAt), display)}
                    {r.missingSince && <span className="block text-sm font-semibold text-bad">{t("notReported")}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {setters.length > 0 && (
        <div className="space-y-2">
          <h3 className="font-semibold">{t("settersHeading")}</h3>
          <dl className="grid gap-x-6 gap-y-1 sm:grid-cols-[max-content_1fr]">
            {setters.map(([key, setter]) => (
              <div key={key} className="contents">
                <dt>{labelOf(key)}</dt>
                <dd>{setter.manual ? t("setByPeople") : setter.source ? <span dir="auto">{setter.source.name}</span> : t("setByRemoved")}</dd>
              </div>
            ))}
          </dl>
        </div>
      )}
      {discovery.software.length > 0 && (
        <div className="overflow-x-auto">
          <table className={tableClass} data-testid="ci-software">
            <caption className="text-start font-semibold">{t("softwareCaption")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("product")}</th>
                <th scope="col" className={cellClass}>{t("publisher")}</th>
                <th scope="col" className={cellClass}>{t("version")}</th>
              </tr>
            </thead>
            <tbody>
              {discovery.software.map((s, i) => (
                <tr key={`${s.name}-${s.version}-${i}`}>
                  <td className={cellClass} dir="auto">{s.name}</td>
                  <td className={cellClass} dir="auto">{s.publisher ?? "—"}</td>
                  <td className={cellClass} dir="ltr">{s.version ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

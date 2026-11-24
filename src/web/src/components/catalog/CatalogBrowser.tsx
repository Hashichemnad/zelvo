import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { textOf } from "@/lib/directory";
import { iconGlyph, type CatalogBrowse, type PublishedServiceSummary } from "@/lib/catalog";
import type { DigitSystem } from "@/lib/format";
import { formatNumber } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { Localized } from "@/components/directory/common";

/** The fee of a service as people read it: "Free" or the amount in AED, in the user's digits. */
export function FeeText({ paid, amountAed, locale, digits }: { paid: boolean; amountAed: number | null; locale: Locale; digits: DigitSystem }) {
  const t = useTranslations("catalog.browse");
  if (!paid) return <>{t("free")}</>;
  return <>{t("fee", { amount: amountAed === null ? "" : formatNumber(amountAed, locale, digits) })}</>;
}

/** A service's icon or image as a tile; decorative, as the name carries the meaning. */
export function ServiceIcon({ service }: { service: Pick<PublishedServiceSummary, "id" | "icon" | "imageFileId"> }) {
  return service.imageFileId ? (
    // eslint-disable-next-line @next/next/no-img-element -- service images come through the API proxy
    <img src={`/bff/api/v1/catalog/published/${service.id}/files/${service.imageFileId}`} alt="" className="size-12 rounded-md border border-line object-cover" />
  ) : (
    <span aria-hidden="true" className="flex size-12 items-center justify-center rounded-md bg-surface-muted text-2xl">{iconGlyph[service.icon ?? "service"] ?? "◆"}</span>
  );
}

/** One service as a card in the catalog: icon, name, summary, fee, days and sub-services. */
export function ServiceTile({ service, locale, digits }: { service: PublishedServiceSummary; locale: Locale; digits: DigitSystem }) {
  const t = useTranslations("catalog.browse");
  return (
    <li className="flex gap-3 rounded-lg border border-line p-4" data-testid="catalog-service">
      <ServiceIcon service={service} />
      <div className="min-w-0 space-y-1">
        <Link href={`/catalog/${service.id}`} className="text-lg font-semibold text-brand underline-offset-4 hover:underline">
          <Localized text={service.name} locale={locale} />
        </Link>
        {textOf(service.summary, locale) && <p><Localized text={service.summary} locale={locale} /></p>}
        <p className="flex flex-wrap gap-x-4 text-sm text-ink-muted">
          {service.type === "External" ? <span>{t("external")}</span> : <span><FeeText paid={service.paid} amountAed={service.amountAed} locale={locale} digits={digits} /></span>}
          {service.days !== null && <span>{t("days", { count: service.days, days: formatNumber(service.days, locale, digits) })}</span>}
          {service.subServiceCount > 0 && <span>{t("subServices", { count: service.subServiceCount })}</span>}
        </p>
        {service.state === "Suspended" && <span className="inline-block rounded-full border border-warn px-2 py-0.5 text-xs font-semibold text-warn">{t("suspended")}</span>}
      </div>
    </li>
  );
}

/**
 * The catalog for requesters and visitors (task C2): a filter box, the catalogs as links and the
 * services the caller may see. Plain links and a GET form, so it works without scripts too.
 */
export function CatalogBrowser({ data, query, catalogId, locale, digits }: { data: CatalogBrowse; query: string; catalogId: string | null; locale: Locale; digits: DigitSystem }) {
  const t = useTranslations("catalog.browse");
  return (
    <div className="space-y-6">
      <form role="search" method="get" className="flex flex-wrap items-end gap-2" aria-label={t("searchLabel")}>
        <div className="min-w-64 flex-1 space-y-1">
          <label htmlFor="catalog-q" className="block font-semibold">{t("searchLabel")}</label>
          <input id="catalog-q" name="q" type="search" defaultValue={query} dir="auto"
            className="w-full rounded-md border border-line bg-surface px-3 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand" />
        </div>
        {catalogId && <input type="hidden" name="catalog" value={catalogId} />}
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">{t("search")}</button>
      </form>
      {data.catalogs.length > 0 && (
        <nav aria-label={t("catalogs")}>
          <ul className="flex flex-wrap gap-2">
            <li>
              <Link href="/catalog" aria-current={catalogId ? undefined : "page"}
                className={`inline-block rounded-full border px-3 py-1 text-sm font-semibold ${catalogId ? "border-line" : "border-brand bg-brand text-surface"}`}>{t("allCatalogs")}</Link>
            </li>
            {data.catalogs.map((c) => (
              <li key={c.id}>
                <Link href={`/catalog?catalog=${c.id}`} aria-current={catalogId === c.id ? "page" : undefined}
                  className={`inline-block rounded-full border px-3 py-1 text-sm font-semibold ${catalogId === c.id ? "border-brand bg-brand text-surface" : "border-line"}`}>
                  <Localized text={c.name} locale={locale} /> <span className="font-normal">({formatNumber(c.serviceCount, locale, digits)})</span>
                </Link>
              </li>
            ))}
          </ul>
        </nav>
      )}
      {data.services.length === 0 ? (
        <p data-testid="catalog-empty">{query ? t("noMatches") : t("empty")}</p>
      ) : (
        <ul className="grid gap-3 sm:grid-cols-2" aria-label={t("services")}>
          {data.services.map((s) => <ServiceTile key={s.id} service={s} locale={locale} digits={digits} />)}
        </ul>
      )}
    </div>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { CatalogBrowser } from "@/components/catalog/CatalogBrowser";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import { loadHelp } from "@/lib/knowledge-pages";
import type { CatalogBrowse } from "@/lib/catalog";
import type { Locale } from "@/lib/incidents";

export async function generateMetadata({ params }: PageProps<"/[locale]/catalog">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.browse" });
  return { title: t("title") };
}

/** The service catalog (task C2): services the caller may see; visitors see public services. */
export default async function CatalogPage({ params, searchParams }: PageProps<"/[locale]/catalog">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "catalog.browse" });
  const search = await searchParams;
  const q = typeof search.q === "string" ? search.q.slice(0, 100) : "";
  const catalog = typeof search.catalog === "string" && isId(search.catalog) ? search.catalog : null;
  const query = new URLSearchParams({ topLevel: q ? "false" : "true" });
  if (q) query.set("q", q);
  if (catalog) query.set("catalogId", catalog);
  const [{ result }, display] = await Promise.all([loadHelp<CatalogBrowse>(`/api/v1/catalog/published?${query}`), getDisplayFormat()]);

  return (
    <section aria-labelledby="catalog-heading" className="space-y-6">
      <h1 id="catalog-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-2xl">{t("intro")}</p>
      {!result ? <p role="alert">{t("unavailable")}</p> : <CatalogBrowser data={result} query={q} catalogId={catalog} locale={locale as Locale} digits={display.digits} />}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { SearchIndexPanel } from "@/components/settings/SearchIndexPanel";
import { getJsonAs, type SearchStatus } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/search">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.search" });
  return { title: t("title") };
}

/** Settings → Search (module 10): the tenant's search index status and "Rebuild index". */
export default async function SearchSettingsPage({ params }: PageProps<"/[locale]/settings/search">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/search`);
  const [t, tSection, tSettings, display] = await Promise.all([
    getTranslations({ locale, namespace: "settings.search" }),
    getTranslations({ locale, namespace: "settings.sections.search" }),
    getTranslations({ locale, namespace: "settings" }),
    getDisplayFormat(),
  ]);
  const status = user?.permissions.includes("search.manage")
    ? await getJsonAs<SearchStatus>("/api/v1/search/status", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="search-settings-heading" className="space-y-4">
      <h2 id="search-settings-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!status ? (
        <p role="alert">{tSettings(user?.permissions.includes("search.manage") ? "unavailable" : "noAccess")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <SearchIndexPanel initial={status} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />
        </>
      )}
    </section>
  );
}

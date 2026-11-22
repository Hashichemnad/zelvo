import { getTranslations, setRequestLocale } from "next-intl/server";
import { IncidentTable, Pagination } from "@/components/incidents/IncidentTable";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { IncidentPage, IncidentSettings } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-incidents">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "incidents" });
  return { title: t("myTitle") };
}

/** "My incidents" (requesters; reused by the portal, D2): the incidents the user reported. */
export default async function MyIncidentsPage({ params, searchParams }: PageProps<"/[locale]/my-incidents">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-incidents`);
  const t = await getTranslations({ locale, namespace: "incidents" });
  const requested = Number((await searchParams).page);
  const page = Number.isInteger(requested) && requested > 0 && requested < 10_000 ? requested : 1;
  const [incidents, settings, format] = await Promise.all([
    getJsonAs<IncidentPage>(`/api/v1/incidents?${new URLSearchParams({ mine: "true", page: String(page), pageSize: "25" })}`, session.data.accessToken, tenant),
    getJsonAs<IncidentSettings>("/api/v1/settings/incidents", session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);

  return (
    <section aria-labelledby="my-incidents-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="my-incidents-heading" className="text-3xl font-bold text-brand">
          {t("myTitle")}
        </h1>
        <Link href="/my-incidents/new" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">
          {t("report")}
        </Link>
      </div>
      <p>{t("myIntro")}</p>
      {!incidents ? (
        <p role="alert">{t("unavailable")}</p>
      ) : incidents.items.length === 0 ? (
        <p>{t("myEmpty")}</p>
      ) : (
        <>
          <IncidentTable incidents={incidents.items} settings={settings} basePath="/my-incidents" staff={false} display={{ locale, ...format }} />
          <Pagination page={incidents.page} pageSize={incidents.pageSize} totalCount={incidents.totalCount} query={{}} basePath="/my-incidents" />
        </>
      )}
    </section>
  );
}

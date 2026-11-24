import { getTranslations, setRequestLocale } from "next-intl/server";
import { RequestTable } from "@/components/catalog/ServiceRequests";
import { Pagination } from "@/components/incidents/IncidentTable";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { ServiceRequestPage } from "@/lib/catalog";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-requests">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.requests" });
  return { title: t("myTitle") };
}

/** "My requests" (task C2; reused by the portal, D2): the service requests made by or for the user. */
export default async function MyRequestsPage({ params, searchParams }: PageProps<"/[locale]/my-requests">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-requests`);
  const t = await getTranslations({ locale, namespace: "catalog.requests" });
  const requested = Number((await searchParams).page);
  const page = Number.isInteger(requested) && requested > 0 && requested < 10_000 ? requested : 1;
  const [requests, format] = await Promise.all([
    getJsonAs<ServiceRequestPage>(`/api/v1/service-requests?${new URLSearchParams({ mine: "true", page: String(page), pageSize: "25" })}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="my-requests-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="my-requests-heading" className="text-3xl font-bold text-brand">{t("myTitle")}</h1>
        <Link href="/catalog" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("newRequest")}</Link>
      </div>
      <p>{t("myIntro")}</p>
      {!requests ? <p role="alert">{t("unavailable")}</p> : requests.items.length === 0 ? <p>{t("myEmpty")}</p> : (
        <>
          <RequestTable requests={requests.items} basePath="/my-requests" staff={false} display={{ locale, ...format }} />
          <Pagination page={requests.page} pageSize={requests.pageSize} totalCount={requests.totalCount} query={{}} basePath="/my-requests" />
        </>
      )}
    </section>
  );
}

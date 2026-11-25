import { getTranslations, setRequestLocale } from "next-intl/server";
import { AgentsView, OverviewView } from "@/components/reports/DashboardViews";
import { ReportShell } from "@/components/reports/ReportShell";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { loadFilterGroups, loadReportScope } from "@/lib/report-pages";
import { filterQuery, readFilter, type AgentsReport, type OverviewReport } from "@/lib/reports";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/reports">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "reports" });
  return { title: t("title") };
}

/**
 * Reports (module 10, task F2): the key figures of the period with the tenant's targets and the
 * dashboards. Staff who see only their own figures get "My performance": their key figures and
 * their own row of agent performance.
 */
export default async function ReportsPage({ params, searchParams }: PageProps<"/[locale]/reports">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/reports`);
  const t = await getTranslations({ locale, namespace: "reports" });
  const token = session.data.accessToken;
  const scope = user ? await loadReportScope(token, tenant) : null;
  if (scope === "forbidden" || scope === null) {
    return <p role="alert" data-testid="reports-no-access">{t(scope === "forbidden" ? "noAccess" : "unavailable")}</p>;
  }
  const filter = readFilter(await searchParams);
  const query = filterQuery(filter);
  const own = scope.mode === "Own";
  const [overview, agents, groups, format] = await Promise.all([
    getJsonAs<OverviewReport>(`/api/v1/reports/overview?${query}`, token, tenant),
    own ? getJsonAs<AgentsReport>(`/api/v1/reports/agents?${query}`, token, tenant) : Promise.resolve(null),
    loadFilterGroups(scope, user?.permissions ?? [], token, tenant),
    getDisplayFormat(),
  ]);
  const display = { locale, calendar: format.calendar, digits: format.digits };
  return (
    <ReportShell kind="Overview" locale={locale} scope={scope} filter={filter} query={query} groups={groups} csrfToken={session.data.csrfToken} showInterval={false}>
      {!overview ? <p role="alert">{t("unavailable")}</p> : <OverviewView report={overview} display={display} own={own} />}
      {own && agents && <AgentsView report={agents} display={display} />}
    </ReportShell>
  );
}

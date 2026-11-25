import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { AgentsView, BacklogView, HappinessView, SlaView, VolumesView } from "@/components/reports/DashboardViews";
import { ReportShell } from "@/components/reports/ReportShell";
import { redirect } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { loadFilterGroups, loadReportScope } from "@/lib/report-pages";
import {
  apiSegment, dashboardOf, filterQuery, readFilter, type AgentsReport, type BacklogReport, type HappinessReport, type SlaReport, type VolumesReport,
} from "@/lib/reports";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/reports/[kind]">) {
  const { locale, kind } = await params;
  const dashboard = dashboardOf(kind);
  const t = await getTranslations({ locale, namespace: "reports" });
  return { title: dashboard ? t(`dashboards.${dashboard.kind}.title`) : t("title") };
}

/**
 * One dashboard of module 10 (task F2): volumes, backlog, SLA compliance, agent performance or
 * happiness, with its filter and exports. People who see only their own figures go to "My
 * performance" instead.
 */
export default async function DashboardPage({ params, searchParams }: PageProps<"/[locale]/reports/[kind]">) {
  const { locale, kind } = await params;
  const dashboard = dashboardOf(kind);
  if (!dashboard) notFound();
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/reports/${kind}`);
  const t = await getTranslations({ locale, namespace: "reports" });
  const token = session.data.accessToken;
  const scope = user ? await loadReportScope(token, tenant) : null;
  if (scope === "forbidden" || scope === null) {
    return <p role="alert" data-testid="reports-no-access">{t(scope === "forbidden" ? "noAccess" : "unavailable")}</p>;
  }
  if (scope.mode === "Own") {
    redirect({ href: "/reports", locale });
  }
  const filter = readFilter(await searchParams);
  const query = filterQuery(filter);
  const path = `/api/v1/reports/${apiSegment(dashboard.kind)}?${query}`;
  const [report, groups, format] = await Promise.all([
    getJsonAs<unknown>(path, token, tenant),
    loadFilterGroups(scope, user?.permissions ?? [], token, tenant),
    getDisplayFormat(),
  ]);
  const display = { locale, calendar: format.calendar, digits: format.digits };
  let body;
  if (!report) body = <p role="alert">{t("unavailableFilter")}</p>;
  else if (dashboard.kind === "Volumes") body = <VolumesView report={report as VolumesReport} display={display} />;
  else if (dashboard.kind === "Backlog") body = <BacklogView report={report as BacklogReport} display={display} />;
  else if (dashboard.kind === "Sla") body = <SlaView report={report as SlaReport} display={display} />;
  else if (dashboard.kind === "Agents") body = <AgentsView report={report as AgentsReport} display={display} />;
  else body = <HappinessView report={report as HappinessReport} display={display} />;
  return (
    <ReportShell kind={dashboard.kind} locale={locale} scope={scope} filter={filter} query={query} groups={groups} csrfToken={session.data.csrfToken}
      showInterval={dashboard.kind !== "Agents"}>
      {body}
    </ReportShell>
  );
}

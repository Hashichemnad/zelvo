import type { ReactNode } from "react";
import { getTranslations } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { ExportPdfButton } from "@/components/ExportPdfButton";
import { apiSegment, dashboards, nameIn, type ReportFilterValues, type ReportKind, type ReportName, type ReportScope } from "@/lib/reports";
import { ReportFilters } from "./ReportFilters";

/**
 * The frame of every dashboard (task F2): the menu of dashboards, what the person sees, the
 * filter, the exports (CSV, Excel, PDF) and "Schedule this report", then the dashboard itself.
 */
export async function ReportShell({ kind, locale, scope, filter, query, groups, csrfToken, children, showInterval = true }: {
  kind: ReportKind;
  locale: string;
  scope: ReportScope;
  filter: ReportFilterValues;
  /** The API query of the filter. */
  query: string;
  groups: { id: string; name: ReportName }[];
  csrfToken: string;
  children: ReactNode;
  showInterval?: boolean;
}) {
  const t = await getTranslations({ locale, namespace: "reports" });
  const own = scope.mode === "Own";
  const title = own ? t("myPerformance.title") : t(`dashboards.${kind}.title`);
  const segment = apiSegment(kind);
  const table = (format: "csv" | "xlsx") => `/bff/api/v1/reports/${segment}/table?${new URLSearchParams([...new URLSearchParams(query), ["format", format]])}`;
  const scheduleQuery = new URLSearchParams({ report: kind, ...(filter.types.length ? { types: filter.types.join(",") } : {}), ...(filter.groups.length ? { groups: filter.groups.join(",") } : {}) });

  return (
    <section aria-labelledby="report-heading" className="space-y-6">
      {kind !== "Overview" && <Link href="/reports" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>}
      <h1 id="report-heading" className="text-3xl font-bold text-brand">{title}</h1>
      <p className="max-w-3xl">{own ? t("myPerformance.intro") : t(`dashboards.${kind}.description`)}</p>
      <p className="text-sm text-ink-muted" data-testid="report-scope">
        {scope.mode === "Groups" ? t("scope.Groups", { groups: scope.groups.map((g) => nameIn(g.name, locale)).join(locale === "ar" ? "، " : ", ") }) : t(`scope.${scope.mode}`)}
      </p>
      {!own && (
        <nav aria-label={t("menuLabel")}>
          <ul className="flex flex-wrap gap-2 text-sm font-semibold">
            <li><Link href="/reports" aria-current={kind === "Overview" ? "page" : undefined} className={pill(kind === "Overview")}>{t("dashboards.Overview.title")}</Link></li>
            {dashboards.map((d) => (
              <li key={d.slug}>
                <Link href={`/reports/${d.slug}`} aria-current={kind === d.kind ? "page" : undefined} className={pill(kind === d.kind)}>{t(`dashboards.${d.kind}.title`)}</Link>
              </li>
            ))}
            {scope.canSchedule && <li><Link href="/reports/schedules" className={pill(false)}>{t("schedules.title")}</Link></li>}
          </ul>
        </nav>
      )}
      <ReportFilters values={filter} types={scope.types} groups={own ? [] : groups} showInterval={showInterval} />
      <div className="flex flex-wrap items-center gap-4" role="group" aria-label={t("actions.label")}>
        <a href={table("csv")} className="text-sm font-semibold text-brand underline-offset-4 hover:underline" data-testid="report-csv">{t("actions.csv")}</a>
        <a href={table("xlsx")} className="text-sm font-semibold text-brand underline-offset-4 hover:underline" data-testid="report-xlsx">{t("actions.xlsx")}</a>
        <ExportPdfButton path={`/reports/${segment}`} query={query} csrfToken={csrfToken} />
        {scope.canSchedule && (
          <Link href={`/reports/schedules/new?${scheduleQuery}`} className="text-sm font-semibold text-brand underline-offset-4 hover:underline" data-testid="report-schedule">
            {t("actions.schedule")}
          </Link>
        )}
      </div>
      {children}
    </section>
  );
}

function pill(current: boolean): string {
  return `block rounded-md px-3 py-1.5 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand ${current ? "bg-brand text-surface" : "border border-line text-ink hover:bg-surface-muted"}`;
}

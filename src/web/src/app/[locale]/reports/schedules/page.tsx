import { getTranslations, setRequestLocale } from "next-intl/server";
import { ScheduleList } from "@/components/reports/ScheduleList";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { loadReportScope } from "@/lib/report-pages";
import type { ReportSchedule } from "@/lib/reports";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/reports/schedules">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "reports.schedules" });
  return { title: t("title") };
}

/** Scheduled reports (task F2): the ones the person owns or receives; every one for reports managers. */
export default async function SchedulesPage({ params }: PageProps<"/[locale]/reports/schedules">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/reports/schedules`);
  const t = await getTranslations({ locale, namespace: "reports" });
  const scope = user ? await loadReportScope(session.data.accessToken, tenant) : null;
  if (scope === "forbidden" || scope === null || !scope.canSchedule) {
    return <p role="alert" data-testid="reports-no-access">{t(scope === null ? "unavailable" : "schedules.noAccess")}</p>;
  }
  const [schedules, format] = await Promise.all([
    getJsonAs<ReportSchedule[]>("/api/v1/reports/schedules", session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="schedules-heading" className="space-y-6">
      <Link href="/reports" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <h1 id="schedules-heading" className="text-3xl font-bold text-brand">{t("schedules.title")}</h1>
      <p className="max-w-3xl">{t("schedules.intro")}</p>
      <Link href="/reports/schedules/new" className="inline-block rounded-md bg-brand px-4 py-2 font-semibold text-surface" data-testid="new-schedule">{t("schedules.new")}</Link>
      {!schedules ? <p role="alert">{t("unavailable")}</p> : (
        <ScheduleList schedules={schedules} csrfToken={session.data.csrfToken} display={{ locale, ...format }} />
      )}
    </section>
  );
}

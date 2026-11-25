import { getTranslations, setRequestLocale } from "next-intl/server";
import { ScheduleForm } from "@/components/reports/ScheduleForm";
import { newSchedule } from "@/components/reports/schedule-defaults";
import { Link } from "@/i18n/navigation";
import { loadFilterGroups, loadReportScope } from "@/lib/report-pages";
import { readFilter, type ReportKind } from "@/lib/reports";
import { requireSignedIn } from "@/lib/signed-in";

const kinds: ReportKind[] = ["Overview", "Volumes", "Backlog", "Sla", "Agents", "Happiness"];

export async function generateMetadata({ params }: PageProps<"/[locale]/reports/schedules/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "reports.schedules" });
  return { title: t("new") };
}

/** A new scheduled report, starting from a dashboard's filter when the person came from one. */
export default async function NewSchedulePage({ params, searchParams }: PageProps<"/[locale]/reports/schedules/new">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/reports/schedules/new`);
  const t = await getTranslations({ locale, namespace: "reports" });
  const scope = user ? await loadReportScope(session.data.accessToken, tenant) : null;
  if (scope === "forbidden" || scope === null || !scope.canSchedule || !user) {
    return <p role="alert" data-testid="reports-no-access">{t(scope === null ? "unavailable" : "schedules.noAccess")}</p>;
  }
  const search = await searchParams;
  const filter = readFilter(search);
  const report = typeof search.report === "string" && (kinds as string[]).includes(search.report) ? (search.report as ReportKind) : "Overview";
  const groups = await loadFilterGroups(scope, user.permissions, session.data.accessToken, tenant);
  const me = { id: user.id, name: user.displayName ?? user.email ?? "" };
  return (
    <section aria-labelledby="schedule-heading" className="space-y-6">
      <Link href="/reports/schedules" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("schedules.back")}</Link>
      <h1 id="schedule-heading" className="text-3xl font-bold text-brand">{t("schedules.new")}</h1>
      <p className="max-w-3xl">{t("schedules.formIntro")}</p>
      <ScheduleForm initial={newSchedule(me, report, filter.types, filter.groups)} types={scope.types} groups={groups}
        canChooseRecipients={scope.canManageSchedules} csrfToken={session.data.csrfToken} />
    </section>
  );
}

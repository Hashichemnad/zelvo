import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { fromSchedule, ScheduleForm } from "@/components/reports/ScheduleForm";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";
import { loadFilterGroups, loadReportScope } from "@/lib/report-pages";
import { formatDay, type ReportDelivery, type ReportSchedule } from "@/lib/reports";
import { requireSignedIn } from "@/lib/signed-in";

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata({ params }: PageProps<"/[locale]/reports/schedules/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "reports.schedules" });
  return { title: t("edit") };
}

/** A scheduled report: its settings (for its owner and reports managers) and the delivery log of the last 30 days. */
export default async function SchedulePage({ params }: PageProps<"/[locale]/reports/schedules/[id]">) {
  const { locale, id } = await params;
  if (!idPattern.test(id)) notFound();
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/reports/schedules/${id}`);
  const t = await getTranslations({ locale, namespace: "reports" });
  const token = session.data.accessToken;
  const scope = user ? await loadReportScope(token, tenant) : null;
  if (scope === "forbidden" || scope === null || !user) {
    return <p role="alert" data-testid="reports-no-access">{t(scope === null ? "unavailable" : "schedules.noAccess")}</p>;
  }
  const [schedule, deliveries, groups, format] = await Promise.all([
    getJsonAs<ReportSchedule>(`/api/v1/reports/schedules/${id}`, token, tenant),
    getJsonAs<ReportDelivery[]>(`/api/v1/reports/schedules/${id}/deliveries`, token, tenant),
    loadFilterGroups(scope, user.permissions, token, tenant),
    getDisplayFormat(),
  ]);
  if (!schedule) {
    return <p role="alert">{t("schedules.notFound")}</p>;
  }
  const display = { locale, ...format };
  const cell = "border-b border-line px-2 py-1 text-start align-top";
  return (
    <section aria-labelledby="schedule-heading" className="space-y-6">
      <Link href="/reports/schedules" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("schedules.back")}</Link>
      <h1 id="schedule-heading" className="text-3xl font-bold text-brand"><bdi>{schedule.name}</bdi></h1>
      <p className="text-sm text-ink-muted">{t("schedules.ownedBy", { name: schedule.owner.name })}</p>
      {schedule.canEdit ? (
        <ScheduleForm initial={fromSchedule(schedule)} types={scope.types} groups={groups} canChooseRecipients={scope.canManageSchedules} csrfToken={session.data.csrfToken} />
      ) : (
        <p>{t("schedules.receiveOnly")}</p>
      )}
      <section aria-labelledby="deliveries-heading" className="space-y-2">
        <h2 id="deliveries-heading" className="text-xl font-semibold">{t("schedules.deliveries")}</h2>
        <p className="text-sm text-ink-muted">{t("schedules.deliveriesIntro")}</p>
        {!deliveries || deliveries.length === 0 ? <p className="text-ink-muted">{t("schedules.noDeliveries")}</p> : (
          <div className="overflow-x-auto">
            <table className="w-full border-collapse text-sm" data-testid="delivery-log">
              <caption className="sr-only">{t("schedules.deliveries")}</caption>
              <thead><tr>
                <th scope="col" className={cell}>{t("schedules.recipient")}</th>
                <th scope="col" className={cell}>{t("schedules.period")}</th>
                <th scope="col" className={cell}>{t("schedules.status")}</th>
                <th scope="col" className={cell}>{t("schedules.sentAt")}</th>
                <th scope="col" className={cell}>{t("schedules.error")}</th>
              </tr></thead>
              <tbody>
                {deliveries.map((d) => (
                  <tr key={d.id}>
                    <th scope="row" className={`${cell} font-normal`}><bdi>{d.recipientName}</bdi></th>
                    <td className={cell}>{t("period", { from: formatDay(d.periodFrom, display), to: formatDay(d.periodTo, display) })}</td>
                    <td className={cell}>{t(`schedules.statuses.${d.status}`)}</td>
                    <td className={cell}>{d.sentAt ? formatDateTime(new Date(d.sentAt), display) : "—"}</td>
                    <td className={cell}>{d.error ? <span dir="ltr" lang="en">{d.error}</span> : ""}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </section>
  );
}

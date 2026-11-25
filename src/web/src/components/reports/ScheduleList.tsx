"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { SaveStatusLine, useSaver } from "@/components/directory/common";
import { fieldButton } from "@/components/forms/ui";
import { Link, useRouter } from "@/i18n/navigation";
import { deleteRequest, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { ReportSchedule } from "@/lib/reports";

/** The scheduled reports the person owns or receives, with "Send now" and delete for those they may change. */
export function ScheduleList({ schedules, csrfToken, display }: { schedules: ReportSchedule[]; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("reports.schedules");
  const tReports = useTranslations("reports");
  const router = useRouter();
  const { status, run, busy } = useSaver();
  const [confirming, setConfirming] = useState<string | null>(null);
  const cell = "border-b border-line px-2 py-2 text-start align-top";
  const when = (s: ReportSchedule) => {
    const time = <bdi dir="ltr">{s.time}</bdi>;
    if (s.frequency === "Weekly") return <>{t("everyWeek", { day: t(`days.${s.dayOfWeek ?? 1}`) })} · {time}</>;
    if (s.frequency === "Monthly") return <>{t("everyMonth", { day: String(s.dayOfMonth ?? 1) })} · {time}</>;
    return <>{t("everyDay")} · {time}</>;
  };

  if (schedules.length === 0) return <p className="text-ink-muted">{t("none")}</p>;
  return (
    <div className="space-y-2">
      <div className="overflow-x-auto">
        <table className="w-full border-collapse text-sm" data-testid="schedule-table">
          <caption className="sr-only">{t("title")}</caption>
          <thead><tr>
            <th scope="col" className={cell}>{t("name")}</th>
            <th scope="col" className={cell}>{t("report")}</th>
            <th scope="col" className={cell}>{t("when")}</th>
            <th scope="col" className={cell}>{t("recipients")}</th>
            <th scope="col" className={cell}>{t("nextRun")}</th>
            <th scope="col" className={cell}><span className="sr-only">{t("actions")}</span></th>
          </tr></thead>
          <tbody>
            {schedules.map((s) => (
              <tr key={s.id}>
                <th scope="row" className={`${cell} font-semibold`}>
                  <Link href={`/reports/schedules/${s.id}`} className="text-brand underline-offset-4 hover:underline"><bdi>{s.name}</bdi></Link>
                  {!s.active && <span className="ms-2 rounded bg-surface-muted px-2 text-xs">{t("paused")}</span>}
                </th>
                <td className={cell}>{tReports(`dashboards.${s.report}.title`)}</td>
                <td className={cell}>{when(s)}</td>
                <td className={cell}>{s.recipients.map((r) => r.name).join(", ")}</td>
                <td className={cell}>{s.nextRunAt ? formatDateTime(new Date(s.nextRunAt), display) : t("notPlanned")}</td>
                <td className={cell}>
                  {s.canEdit && (
                    <span className="flex flex-wrap gap-2">
                      <button type="button" className={fieldButton} disabled={busy}
                        onClick={() => void run(async () => { await postJson(`/v1/reports/schedules/${s.id}/send-now`, undefined, csrfToken); router.refresh(); })}>
                        {t("sendNow")}
                      </button>
                      {confirming === s.id ? (
                        <>
                          <button type="button" className={`${fieldButton} text-bad`} disabled={busy}
                            onClick={() => void run(async () => { await deleteRequest(`/v1/reports/schedules/${s.id}`, csrfToken); setConfirming(null); router.refresh(); })}>
                            {t("confirmDelete", { name: s.name })}
                          </button>
                          <button type="button" className={fieldButton} onClick={() => setConfirming(null)}>{t("cancel")}</button>
                        </>
                      ) : (
                        <button type="button" className={fieldButton} onClick={() => setConfirming(s.id)}>{t("delete")}</button>
                      )}
                    </span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <SaveStatusLine status={status} />
    </div>
  );
}

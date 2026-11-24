"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { getJson, postJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import type { Page } from "@/lib/integrations";
import { eventKey, type DeliveryStatus, type NotificationDelivery, type NotificationDeliveryDetail } from "@/lib/notifications";
import { cellClass, codeClass, inputClass, secondaryButton, tableClass } from "../settings/integrations/ui";

const statuses: DeliveryStatus[] = ["Pending", "Sent", "Failed"];
const statusClass: Record<DeliveryStatus, string> = { Pending: "text-warn", Sent: "text-ok", Failed: "text-bad" };

/** Settings → Notifications → Email delivery log (kept 30 days; addresses masked until an email is opened, which is audited). */
export function DeliveryLogPanel({ initial, csrfToken, display }: { initial: Page<NotificationDelivery>; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("settings.notifications.deliveries");
  const tEvents = useTranslations("notifications.events");
  const [page, setPage] = useState(initial);
  const [status, setStatus] = useState<DeliveryStatus | "">("");
  const [detail, setDetail] = useState<NotificationDeliveryDetail | null>(null);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const date = (value: string) => formatDateTime(new Date(value), display);

  async function load(number: number, filter: DeliveryStatus | "") {
    try {
      setPage(await getJson<Page<NotificationDelivery>>(`/v1/notifications/deliveries?page=${number}&pageSize=${initial.pageSize}${filter ? `&status=${filter}` : ""}`));
    } catch {
      setMessage({ ok: false, text: t("empty") });
    }
  }

  async function open(id: number) {
    try {
      setDetail(await getJson<NotificationDeliveryDetail>(`/v1/notifications/deliveries/${id}`));
    } catch {
      setDetail(null);
    }
  }

  async function resend(id: number) {
    try {
      await postJson(`/v1/notifications/deliveries/${id}/resend`, undefined, csrfToken);
      setMessage({ ok: true, text: t("resent") });
      await load(1, status);
    } catch {
      setMessage(null);
    }
  }

  return (
    <div className="space-y-4" data-testid="delivery-log">
      <p className="max-w-3xl">{t("intro")}</p>
      <div className="max-w-xs">
        <label htmlFor="delivery-status" className="font-semibold">{t("filter")}</label>
        <select
          id="delivery-status"
          className={inputClass}
          value={status}
          onChange={(e) => {
            const value = e.target.value as DeliveryStatus | "";
            setStatus(value);
            void load(1, value);
          }}
        >
          <option value="">{t("all")}</option>
          {statuses.map((s) => (
            <option key={s} value={s}>{t(`status.${s}`)}</option>
          ))}
        </select>
      </div>
      {message && <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-bad"}>{message.text}</p>}
      {page.items.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <thead>
              <tr>
                {(["event", "to", "subject", "status", "attempts", "when"] as const).map((c) => (
                  <th key={c} scope="col" className={cellClass}>{t(`columns.${c}`)}</th>
                ))}
                <td className={cellClass} />
              </tr>
            </thead>
            <tbody>
              {page.items.map((d) => (
                <tr key={d.id}>
                  <td className={cellClass}>{tEvents(eventKey(d.eventType))}{d.manual && <span className="block text-xs text-ink-muted">{t("manual")}</span>}</td>
                  <td className={cellClass} dir="ltr">{d.address}</td>
                  <td className={cellClass} lang={d.language} dir={d.language === "ar" ? "rtl" : "ltr"}>{d.subject}</td>
                  <td className={cellClass}>
                    <span className={`font-semibold ${statusClass[d.status]}`}>{t(`status.${d.status}`)}</span>
                    {d.lastError && <span className="block text-xs" dir="ltr">{d.lastError}</span>}
                    {d.status === "Pending" && d.nextAttemptAt && <span className="block text-xs">{t("nextAttempt", { date: date(d.nextAttemptAt) })}</span>}
                  </td>
                  <td className={cellClass}>{formatNumber(d.attempts, display.locale, display.digits)}</td>
                  <td className={cellClass}><time dateTime={d.createdAt}>{date(d.createdAt)}</time></td>
                  <td className={cellClass}>
                    <div className="flex flex-wrap gap-2">
                      <button type="button" className={secondaryButton} onClick={() => void open(d.id)}>{t("view")}</button>
                      {d.status !== "Pending" && <button type="button" className={secondaryButton} onClick={() => void resend(d.id)}>{t("resend")}</button>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <div className="flex gap-2">
        {page.page > 1 && <button type="button" className={secondaryButton} onClick={() => void load(page.page - 1, status)}>{t("previous")}</button>}
        {page.page * page.pageSize < page.totalCount && <button type="button" className={secondaryButton} onClick={() => void load(page.page + 1, status)}>{t("next")}</button>}
      </div>
      {detail && (
        <section aria-labelledby="delivery-detail" className="space-y-2 rounded-lg border border-line p-4" data-testid="delivery-detail">
          <div className="flex items-center justify-between gap-2">
            <h3 id="delivery-detail" className="font-semibold">{t("detailHeading", { address: detail.address })}</h3>
            <button type="button" className={secondaryButton} onClick={() => setDetail(null)}>{t("close")}</button>
          </div>
          <iframe title={detail.delivery.subject} sandbox="" srcDoc={detail.html} className="h-[32rem] w-full rounded-md border border-line bg-surface" />
          <pre className={codeClass} lang={detail.delivery.language} dir={detail.delivery.language === "ar" ? "rtl" : "ltr"}>{detail.text}</pre>
        </section>
      )}
    </div>
  );
}

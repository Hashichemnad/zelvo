"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { Localized } from "@/components/directory/common";
import { fieldButton, fieldInput } from "@/components/forms/ui";
import { ApiError, postForm, postJson } from "@/lib/client-api";
import { formatDateTime, formatLongDate, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { formatAed, type Payment, type Reconciliation } from "@/lib/payments";

/**
 * Finance (task E8): payments of the period with their receipts, the Excel export, and the daily
 * reconciliation of each gateway, run again or completed with the gateway's settlement file.
 */
export function PaymentsFinance({ payments, reconciliations, gateways, from, to, display, csrfToken }: {
  payments: Payment[];
  reconciliations: Reconciliation[];
  gateways: { id: string; name: string }[];
  from: string;
  to: string;
  display: DateDisplayOptions;
  csrfToken: string;
}) {
  const t = useTranslations("payments.finance");
  const tState = useTranslations("payments.request.states");
  const locale = useLocale() as Locale;
  const id = useId();
  const [days, setDays] = useState(reconciliations);
  const [gatewayId, setGatewayId] = useState(gateways[0]?.id ?? "");
  const [day, setDay] = useState(to);
  const [status, setStatus] = useState("");
  const cell = "border-b border-line px-2 py-2 text-start align-top";
  const aed = (v: number) => `${formatAed(v, locale, display.digits)} AED`;
  const query = new URLSearchParams({ ...(from ? { from } : {}), ...(to ? { to } : {}) });

  async function run() {
    try {
      const result = await postJson<Reconciliation>("/v1/payments/reconciliations", { gatewayId, day }, csrfToken);
      setDays((list) => [result, ...list.filter((r) => r.id !== result.id)]);
      setStatus(t("reconciled"));
    } catch {
      setStatus(t("failed"));
    }
  }

  async function upload(reconciliation: Reconciliation, file: File) {
    const data = new FormData();
    data.append("file", file, file.name);
    try {
      const { body } = await postForm<Reconciliation>(`/v1/payments/reconciliations/${reconciliation.id}/settlement-file`, data, csrfToken);
      setDays((list) => list.map((r) => (r.id === body.id ? body : r)));
      setStatus(t("imported"));
    } catch (error) {
      setStatus(error instanceof ApiError && error.status === 400 ? t("fileInvalid") : t("failed"));
    }
  }

  return (
    <div className="space-y-8" data-testid="payments-finance">
      <form method="get" className="flex flex-wrap items-end gap-3" aria-label={t("filters")}>
        <div className="space-y-1">
          <label htmlFor={`${id}-from`} className="block text-sm">{t("from")}</label>
          <input id={`${id}-from`} name="from" type="date" dir="ltr" defaultValue={from} className={fieldInput} />
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-to`} className="block text-sm">{t("to")}</label>
          <input id={`${id}-to`} name="to" type="date" dir="ltr" defaultValue={to} className={fieldInput} />
        </div>
        <button type="submit" className={fieldButton}>{t("show")}</button>
        <a href={`/bff/api/v1/payments/export?${query}&format=xlsx`} className={fieldButton}>{t("exportExcel")}</a>
        <a href={`/bff/api/v1/payments/export?${query}&format=csv`} className={fieldButton}>{t("exportCsv")}</a>
      </form>
      <section aria-labelledby={`${id}-payments`} className="space-y-2">
        <h2 id={`${id}-payments`} className="text-xl font-semibold">{t("payments")}</h2>
        {payments.length === 0 ? <p>{t("noPayments")}</p> : (
          <div className="overflow-x-auto">
            <table className="w-full border-collapse" data-testid="payments-table">
              <thead>
                <tr>
                  {["receipt", "request", "service", "payer", "amount", "state", "paidAt"].map((k) => <th key={k} scope="col" className={cell}>{t(`columns.${k}`)}</th>)}
                </tr>
              </thead>
              <tbody>
                {payments.map((p) => (
                  <tr key={p.id}>
                    <td className={cell} dir="ltr">{p.receiptNumber ? <Link href={`/payments/${p.id}/receipt`} className="font-semibold text-brand underline">{p.receiptNumber}</Link> : "—"}</td>
                    <td className={cell} dir="ltr">{p.request ? <Link href={`/requests/${p.request.id}`} className="text-brand underline">{p.request.number}</Link> : "—"}</td>
                    <td className={cell}>{p.service ? <Localized text={p.service.name} locale={locale} /> : "—"}</td>
                    <td className={cell}>{p.payer?.displayName ?? "—"}</td>
                    <td className={cell}>{aed(p.amountAed)}{p.refundedAed > 0 && <span className="block text-sm">{t("refundedAmount", { amount: aed(p.refundedAed) })}</span>}</td>
                    <td className={cell}>{tState(p.state)}</td>
                    <td className={cell}>{p.paidAt ? formatDateTime(new Date(p.paidAt), display) : "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
      <section aria-labelledby={`${id}-recon`} className="space-y-3">
        <h2 id={`${id}-recon`} className="text-xl font-semibold">{t("reconciliation")}</h2>
        <p className="text-sm text-ink-muted">{t("reconciliationHint")}</p>
        <div className="flex flex-wrap items-end gap-3">
          <div className="space-y-1">
            <label htmlFor={`${id}-gateway`} className="block text-sm">{t("gateway")}</label>
            <select id={`${id}-gateway`} className={fieldInput} value={gatewayId} onChange={(e) => setGatewayId(e.target.value)}>
              {gateways.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-day`} className="block text-sm">{t("day")}</label>
            <input id={`${id}-day`} type="date" dir="ltr" className={fieldInput} value={day} onChange={(e) => setDay(e.target.value)} />
          </div>
          <button type="button" className={fieldButton} disabled={!gatewayId || !day} onClick={() => void run()}>{t("run")}</button>
        </div>
        {days.length === 0 ? <p>{t("noReconciliations")}</p> : (
          <div className="overflow-x-auto">
            <table className="w-full border-collapse" data-testid="reconciliations">
              <thead>
                <tr>
                  {["day", "gateway", "matched", "mismatched", "unknown", "total", "file"].map((k) => <th key={k} scope="col" className={cell}>{t(`reconColumns.${k}`)}</th>)}
                </tr>
              </thead>
              <tbody>
                {days.map((r) => (
                  <tr key={r.id}>
                    <td className={cell}>{formatLongDate(new Date(`${r.day}T12:00:00Z`), display)}</td>
                    <td className={cell}>{r.gateway}</td>
                    <td className={cell}>{r.matched}</td>
                    <td className={cell}>{r.mismatched}</td>
                    <td className={cell}>{r.unknownToZelvo}</td>
                    <td className={cell}>{aed(r.totalAed)}</td>
                    <td className={cell}>
                      <label className="block text-sm">
                        <span className="sr-only">{t("settlementFile", { day: r.day })}</span>
                        <input type="file" accept=".csv,.xlsx" className="text-sm" aria-label={t("settlementFile", { day: r.day })}
                          onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ""; if (f) void upload(r, f); }} />
                      </label>
                      <a href={`/bff/api/v1/payments/reconciliations/${r.id}/export?format=xlsx`} className="text-sm text-brand underline">{t("exportLines")}</a>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
      <p role="status" aria-live="polite">{status}</p>
    </div>
  );
}


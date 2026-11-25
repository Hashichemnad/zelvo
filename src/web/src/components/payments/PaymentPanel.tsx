"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { Localized } from "@/components/directory/common";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { ApiError, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { formatAed, type Payment } from "@/lib/payments";

/**
 * The fee of a request (task E8): what it is made of, where it stands and, for the requester,
 * "Pay now", which sends them to the gateway's own page (card details are entered only there).
 * Paid fees link to the receipt; staff who may refund see the refund form.
 */
export function PaymentPanel({ payments, mayPay, mayRefund, display, csrfToken }: {
  payments: Payment[];
  mayPay: boolean;
  mayRefund: boolean;
  display: DateDisplayOptions;
  csrfToken: string;
}) {
  const t = useTranslations("payments.request");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [amount, setAmount] = useState("");
  const [reason, setReason] = useState("");
  if (payments.length === 0) return null;
  const aed = (value: number) => t("aed", { amount: formatAed(value, locale, display.digits) });

  async function pay(payment: Payment) {
    setBusy(true);
    setMessage(t("redirecting"));
    try {
      const { redirectUrl } = await postJson<{ redirectUrl: string }>(`/v1/payments/${payment.id}/pay`, { language: locale }, csrfToken);
      window.location.assign(redirectUrl);
    } catch (error) {
      setBusy(false);
      setMessage(error instanceof ApiError && error.code === "gateway-unavailable" ? t("gatewayUnavailable") : t("failed"));
    }
  }

  async function refund(payment: Payment) {
    setBusy(true);
    try {
      await postJson(`/v1/payments/${payment.id}/refunds`, { amountAed: amount ? Number(amount) : undefined, reason: reason.trim() }, csrfToken);
      setMessage(t("refunded"));
      setAmount("");
      setReason("");
      router.refresh();
    } catch (error) {
      setMessage(error instanceof ApiError && error.code === "refund-failed" ? t("refundFailed") : t("failed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="payment-panel">
      <h2 id={`${id}-heading`} className="text-xl font-semibold">{t("title")}</h2>
      {payments.map((payment) => (
        <div key={payment.id} className="space-y-2" data-testid="payment">
          <p className="font-semibold">
            {t(`states.${payment.state}`)} · {aed(payment.amountAed)}
          </p>
          {payment.lines.length > 0 && (
            <ul className="text-sm">
              {payment.lines.map((line, i) => (
                <li key={i}><Localized text={line.name} locale={locale} />: {aed(line.amountAed)}</li>
              ))}
            </ul>
          )}
          {payment.state === "AwaitingPayment" && payment.expiresAt && <p className="text-sm">{t("dueBy", { date: formatDateTime(new Date(payment.expiresAt), display) })}</p>}
          {payment.state === "AwaitingPayment" && payment.timing === "OnCompletion" && <p className="text-sm">{t("onCompletion")}</p>}
          {payment.attempts.some((a) => a.state === "Failed" || a.state === "Cancelled") && payment.state === "AwaitingPayment" && <p className="text-sm text-warn">{t("lastFailed")}</p>}
          {payment.state === "AwaitingPayment" && mayPay && (
            <div className="space-y-1">
              <button type="button" className={primaryAction} disabled={busy} onClick={() => void pay(payment)}>{t("payNow")}</button>
              <p className="text-sm text-ink-muted">{t("cardNote")}</p>
            </div>
          )}
          {payment.receiptNumber && (
            <p>
              <Link href={`/payments/${payment.id}/receipt`} className="font-semibold text-brand underline">{t("receipt", { number: payment.receiptNumber })}</Link>
              {payment.paidAt && <span className="ms-2 text-sm">{formatDateTime(new Date(payment.paidAt), display)}</span>}
            </p>
          )}
          {payment.refunds.length > 0 && (
            <ul className="text-sm">
              {payment.refunds.map((r) => <li key={r.id}>{t(`refundStates.${r.state}`)}: {aed(r.amountAed)} — <span dir="auto">{r.reason}</span></li>)}
            </ul>
          )}
          {mayRefund && (payment.state === "Paid" || payment.state === "PartlyRefunded") && (
            <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => { e.preventDefault(); void refund(payment); }}>
              <div className="space-y-1">
                <label htmlFor={`${id}-amount`} className="block text-sm">{t("refundAmount")}</label>
                <input id={`${id}-amount`} type="number" min={0.01} step={0.01} dir="ltr" className={fieldInput} value={amount} onChange={(e) => setAmount(e.target.value)} />
              </div>
              <div className="space-y-1">
                <label htmlFor={`${id}-reason`} className="block text-sm">{t("refundReason")}</label>
                <input id={`${id}-reason`} required minLength={2} maxLength={1000} dir="auto" className={fieldInput} value={reason} onChange={(e) => setReason(e.target.value)} />
              </div>
              <button type="submit" className={fieldButton} disabled={busy || reason.trim().length < 2}>{t("refund")}</button>
            </form>
          )}
        </div>
      ))}
      <p role="status" aria-live="polite" className="text-sm">{message}</p>
    </section>
  );
}

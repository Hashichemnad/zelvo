import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { apiFetch } from "@/lib/api";
import type { Payment } from "@/lib/payments";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/payments/return/[reference]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "payments.return" });
  return { title: t("title") };
}

/**
 * Where the gateway sends the payer back (task E8). Whatever the address says, Zelvo asks the
 * gateway's status API (the API does, on the server) and shows what it answered.
 */
export default async function PaymentReturnPage({ params }: PageProps<"/[locale]/payments/return/[reference]">) {
  const { locale, reference } = await params;
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "payments.return" });
  if (!/^[A-Z0-9]{8,40}$/.test(reference)) {
    return <p role="alert">{t("unknown")}</p>;
  }
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/payments/return/${reference}`);
  let payment: Payment | null = null;
  try {
    const response = await apiFetch(`/api/v1/payments/attempts/${reference}/confirm`, session.data.accessToken, tenant, { method: "POST" });
    payment = response.ok ? ((await response.json()) as Payment) : null;
  } catch {
    payment = null;
  }
  const attempt = payment?.attempts.find((a) => a.reference === reference);
  const outcome = !payment ? "unknown" : payment.state === "Paid" || attempt?.state === "Paid" ? "paid" : attempt?.state === "Failed" || attempt?.state === "Cancelled" ? "failed" : "pending";
  return (
    <section aria-labelledby="payment-return-heading" className="max-w-2xl space-y-4" data-testid="payment-return">
      <h1 id="payment-return-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p role="status" className="text-lg font-semibold">{t(`outcomes.${outcome}`)}</p>
      {payment?.receiptNumber && outcome === "paid" && (
        <p><Link href={`/payments/${payment.id}/receipt`} className="font-semibold text-brand underline">{t("receipt", { number: payment.receiptNumber })}</Link></p>
      )}
      {payment?.request && (
        <p><Link href={`/my-requests/${payment.request.id}`} className="font-semibold text-brand underline">{t("backToRequest", { number: payment.request.number })}</Link></p>
      )}
    </section>
  );
}

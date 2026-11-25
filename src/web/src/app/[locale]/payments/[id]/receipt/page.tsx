import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { Localized } from "@/components/directory/common";
import { PrintButton } from "@/components/payments/PrintButton";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";
import { isId } from "@/lib/incident-pages";
import type { Locale } from "@/lib/incidents";
import { formatAed, type Receipt } from "@/lib/payments";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/payments/[id]/receipt">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "payments.receipt" });
  return { title: t("title") };
}

/** The bilingual e-receipt of a payment (task E8), printable; both languages side by side. */
export default async function ReceiptPage({ params }: PageProps<"/[locale]/payments/[id]/receipt">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/payments/${id}/receipt`);
  const [receipt, format, en, ar] = await Promise.all([
    getJsonAs<Receipt>(`/api/v1/payments/${id}/receipt`, session.data.accessToken, tenant),
    getDisplayFormat(),
    getTranslations({ locale: "en", namespace: "payments.receipt" }),
    getTranslations({ locale: "ar", namespace: "payments.receipt" }),
  ]);
  const t = locale === "ar" ? ar : en;
  if (!receipt) {
    return <p role="alert">{t("notFound")}</p>;
  }
  const display = { locale, ...format };
  const rows: [string, React.ReactNode][] = [
    ["number", <span key="n" dir="ltr">{receipt.receiptNumber}</span>],
    ["paidAt", formatDateTime(new Date(receipt.paidAt), display)],
    ["request", receipt.request ? <span key="r" dir="ltr">{receipt.request.number}</span> : "—"],
    ["service", receipt.service ? <Localized key="s" text={receipt.service.name} locale={locale as Locale} /> : "—"],
    ["payer", receipt.payer?.displayName ?? "—"],
    ["transaction", <span key="t" dir="ltr">{receipt.transactionId ?? "—"}</span>],
    ["gateway", receipt.gateway ?? "—"],
  ];
  return (
    <article aria-labelledby="receipt-heading" className="mx-auto max-w-2xl space-y-4 rounded-lg border border-line p-6" data-testid="receipt">
      <header className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <p className="font-semibold"><Localized text={receipt.organisation} locale={locale as Locale} /></p>
          <h1 id="receipt-heading" className="text-2xl font-bold text-brand">{en("title")} · <span lang="ar" dir="rtl">{ar("title")}</span></h1>
        </div>
        <PrintButton label={t("print")} />
      </header>
      <dl className="grid gap-2 sm:grid-cols-2">
        {rows.map(([key, value]) => (
          <div key={key} className="border-b border-line pb-1">
            <dt className="text-sm text-ink-muted">{en(key)} · <span lang="ar" dir="rtl">{ar(key)}</span></dt>
            <dd className="font-semibold">{value}</dd>
          </div>
        ))}
      </dl>
      <table className="w-full border-collapse">
        <caption className="text-start font-semibold">{en("lines")} · <span lang="ar" dir="rtl">{ar("lines")}</span></caption>
        <tbody>
          {receipt.lines.map((line, i) => (
            <tr key={i} className="border-b border-line">
              <th scope="row" className="py-1 text-start font-normal"><Localized text={line.name} locale={locale as Locale} /></th>
              <td className="py-1 text-end" dir="ltr">{formatAed(line.amountAed, locale, display.digits)} AED</td>
            </tr>
          ))}
          <tr>
            <th scope="row" className="py-1 text-start">{en("total")} · <span lang="ar" dir="rtl">{ar("total")}</span></th>
            <td className="py-1 text-end font-bold" dir="ltr">{formatAed(receipt.amountAed, locale, display.digits)} AED</td>
          </tr>
          {receipt.refundedAed > 0 && (
            <tr>
              <th scope="row" className="py-1 text-start">{en("refunded")} · <span lang="ar" dir="rtl">{ar("refunded")}</span></th>
              <td className="py-1 text-end" dir="ltr">{formatAed(receipt.refundedAed, locale, display.digits)} AED</td>
            </tr>
          )}
        </tbody>
      </table>
      <p className="text-sm text-ink-muted">{t("note")}</p>
    </article>
  );
}

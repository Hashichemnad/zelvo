import { getTranslations, setRequestLocale } from "next-intl/server";
import { PaymentsFinance } from "@/components/payments/PaymentsFinance";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { Payment, Reconciliation } from "@/lib/payments";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/payments">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "payments.finance" });
  return { title: t("title") };
}

const isDay = (v: unknown): v is string => typeof v === "string" && /^\d{4}-\d{2}-\d{2}$/.test(v);

/** Payments for finance (task E8): the period's payments, export and daily reconciliation. */
export default async function PaymentsPage({ params, searchParams }: PageProps<"/[locale]/payments">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/payments`);
  const t = await getTranslations({ locale, namespace: "payments.finance" });
  if (!user?.permissions.includes("payments.read")) {
    return <p role="alert">{t(user ? "noAccess" : "unavailable")}</p>;
  }
  const search = await searchParams;
  const now = new Date();
  const today = now.toISOString().slice(0, 10);
  const monthAgo = new Date(now);
  monthAgo.setUTCDate(monthAgo.getUTCDate() - 30);
  const from = isDay(search.from) ? search.from : monthAgo.toISOString().slice(0, 10);
  const to = isDay(search.to) ? search.to : today;
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [payments, reconciliations, format, gateways] = await Promise.all([
    get<{ items: Payment[] }>(`/api/v1/payments?${new URLSearchParams({ from, to, pageSize: "100" })}`),
    get<Reconciliation[]>("/api/v1/payments/reconciliations"),
    getDisplayFormat(),
    user.permissions.includes("payments.manage") ? get<{ id: string; name: string }[]>("/api/v1/payments/gateways") : Promise.resolve(null),
  ]);
  const known = gateways ?? [...new Map((reconciliations ?? []).map((r) => [r.gatewayId, { id: r.gatewayId, name: r.gateway ?? r.gatewayId }])).values()];
  return (
    <section aria-labelledby="payments-heading" className="space-y-6">
      <h1 id="payments-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      {!payments ? <p role="alert">{t("unavailable")}</p> : (
        <PaymentsFinance payments={payments.items} reconciliations={reconciliations ?? []} gateways={known} from={from} to={to}
          display={{ locale, ...format }} csrfToken={session.data.csrfToken} />
      )}
    </section>
  );
}

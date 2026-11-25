import { getTranslations, setRequestLocale } from "next-intl/server";
import { PaymentSettingsPanel } from "@/components/payments/PaymentSettingsPanel";
import { getJsonAs } from "@/lib/api";
import type { Gateway } from "@/lib/payments";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/payments">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.payments" });
  return { title: t("title") };
}

/** Settings → Payments (task E8): the switch, unpaid requests' time and the gateways. */
export default async function PaymentSettingsPage({ params }: PageProps<"/[locale]/settings/payments">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/payments`);
  const [tSection, tSettings, t] = await Promise.all([
    getTranslations({ locale, namespace: "settings.sections.payments" }),
    getTranslations({ locale, namespace: "settings" }),
    getTranslations({ locale, namespace: "payments.settings" }),
  ]);
  const allowed = user?.permissions.includes("payments.manage");
  const [settings, gateways] = allowed
    ? await Promise.all([
      getJsonAs<{ enabled: boolean; unpaidExpiryHours: number | null }>("/api/v1/settings/payments", session.data.accessToken, tenant),
      getJsonAs<Gateway[]>("/api/v1/payments/gateways", session.data.accessToken, tenant),
    ])
    : [null, null];
  return (
    <section aria-labelledby="payment-settings-heading" className="space-y-4">
      <h2 id="payment-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!settings || !gateways ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <PaymentSettingsPanel initialSettings={settings} initialGateways={gateways} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

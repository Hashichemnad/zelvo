import { getTranslations, setRequestLocale } from "next-intl/server";
import { DeliveryLogPanel } from "@/components/notifications/DeliveryLogPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { Page } from "@/lib/integrations";
import type { NotificationDelivery } from "@/lib/notifications";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/notifications/deliveries">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.notifications.tabs" });
  return { title: t("deliveries") };
}

/** Settings → Notifications → Email delivery log. */
export default async function NotificationDeliveriesPage({ params }: PageProps<"/[locale]/settings/notifications/deliveries">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/notifications/deliveries`);
  if (!user?.permissions.includes("notifications.manage")) {
    return null;
  }
  const [deliveries, display, t] = await Promise.all([
    getJsonAs<Page<NotificationDelivery>>("/api/v1/notifications/deliveries?pageSize=25", session.data.accessToken, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!deliveries) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <DeliveryLogPanel initial={deliveries} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />;
}

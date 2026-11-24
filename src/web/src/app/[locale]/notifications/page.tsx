import { getTranslations, setRequestLocale } from "next-intl/server";
import { NotificationList } from "@/components/notifications/NotificationList";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { Page } from "@/lib/integrations";
import type { AppNotification } from "@/lib/notifications";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/notifications">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "notifications" });
  return { title: t("title") };
}

/** The signed-in person's notifications (module 10, task B5). */
export default async function NotificationsPage({ params }: PageProps<"/[locale]/notifications">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/notifications`);
  const [t, display, notifications] = await Promise.all([
    getTranslations({ locale, namespace: "notifications" }),
    getDisplayFormat(),
    getJsonAs<Page<AppNotification>>("/api/v1/me/notifications?pageSize=25", session.data.accessToken, tenant),
  ]);
  return (
    <section aria-labelledby="notifications-heading" className="space-y-4">
      <h1 id="notifications-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-2xl">{t("intro")}</p>
      {notifications ? (
        <NotificationList initial={notifications} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />
      ) : (
        <p role="alert">{t("unavailable")}</p>
      )}
    </section>
  );
}

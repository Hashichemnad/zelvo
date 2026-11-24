import { getTranslations, setRequestLocale } from "next-intl/server";
import { NotificationsTabs } from "@/components/notifications/NotificationsTabs";
import { requireSignedIn } from "@/lib/signed-in";

/** Settings → Notifications (module 10, task B5): heading, introduction and the page tabs. */
export default async function NotificationSettingsLayout({ children, params }: LayoutProps<"/[locale]/settings/notifications">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { user } = await requireSignedIn(locale, `/${locale}/settings/notifications`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.notifications" }),
    getTranslations({ locale, namespace: "settings.sections.notifications" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("notifications.manage") ?? false;

  return (
    <section aria-labelledby="notifications-settings-heading" className="space-y-4">
      <h2 id="notifications-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!allowed ? (
        <p role="alert">{tSettings("noAccess")}</p>
      ) : (
        <>
          <p className="max-w-3xl">{t("intro")}</p>
          <NotificationsTabs />
          {children}
        </>
      )}
    </section>
  );
}

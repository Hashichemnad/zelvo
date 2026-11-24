import { getTranslations, setRequestLocale } from "next-intl/server";
import { NotificationSettingsPanel } from "@/components/notifications/NotificationSettingsPanel";
import { getJsonAs } from "@/lib/api";
import type { NotificationSettings } from "@/lib/notifications";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/notifications">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.notifications" });
  return { title: t("title") };
}

/** Settings → Notifications → Events and wording. */
export default async function NotificationSettingsPage({ params }: PageProps<"/[locale]/settings/notifications">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/notifications`);
  if (!user?.permissions.includes("notifications.manage")) {
    return null;
  }
  const [settings, t] = await Promise.all([
    getJsonAs<NotificationSettings>("/api/v1/notifications/settings", session.data.accessToken, tenant),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!settings) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <NotificationSettingsPanel initial={settings} csrfToken={session.data.csrfToken} />;
}

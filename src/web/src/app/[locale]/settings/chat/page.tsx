import { getTranslations, setRequestLocale } from "next-intl/server";
import { ChatWidgetsPanel } from "@/components/channels/ChatWidgetsPanel";
import { getJsonAs } from "@/lib/api";
import { getRequestAuthConfig } from "@/lib/auth/config";
import type { ChatWidget } from "@/lib/channels";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/chat">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "channels.widgets" });
  return { title: t("title") };
}

/** Settings → Live chat (module 6, task E2): chat widgets for the portal and other websites. */
export default async function LiveChatSettingsPage({ params }: PageProps<"/[locale]/settings/chat">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/chat`);
  const t = await getTranslations({ locale, namespace: "channels.widgets" });
  if (!user?.permissions.includes("channels.manage")) {
    return null;
  }
  const [widgets, choices, config] = await Promise.all([
    getJsonAs<ChatWidget[]>("/api/v1/chat/widgets", session.data.accessToken, tenant),
    loadDirectoryChoices(session.data.accessToken, tenant, user.permissions),
    getRequestAuthConfig(),
  ]);
  if (!widgets) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <section aria-labelledby="widgets-heading" className="space-y-4">
      <h2 id="widgets-heading" className="text-2xl font-semibold">{t("title")}</h2>
      <ChatWidgetsPanel initial={widgets} csrfToken={session.data.csrfToken} appUrl={config.appUrl.toString()} categories={choices.categories} groups={choices.groups ?? []} />
    </section>
  );
}

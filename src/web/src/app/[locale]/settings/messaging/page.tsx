import { getTranslations, setRequestLocale } from "next-intl/server";
import { MessagingPanel } from "@/components/channels/MessagingPanel";
import { getJsonAs } from "@/lib/api";
import type { MessagingChannel } from "@/lib/messaging";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/messaging">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "channels.messaging" });
  return { title: t("title") };
}

/** Settings → Messaging (module 6, task E3): WhatsApp, SMS gateways and the Teams app. */
export default async function MessagingPage({ params }: PageProps<"/[locale]/settings/messaging">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/messaging`);
  const t = await getTranslations({ locale, namespace: "channels.messaging" });
  if (!user?.permissions.includes("channels.manage")) {
    return null;
  }
  const [channels, choices, display] = await Promise.all([
    getJsonAs<MessagingChannel[]>("/api/v1/messaging/channels", session.data.accessToken, tenant),
    loadDirectoryChoices(session.data.accessToken, tenant, user.permissions),
    getDisplayFormat(),
  ]);
  if (!channels) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <section aria-labelledby="messaging-heading" className="space-y-4">
      <h2 id="messaging-heading" className="text-2xl font-semibold">{t("title")}</h2>
      <MessagingPanel initial={channels} csrfToken={session.data.csrfToken} categories={choices.categories} groups={choices.groups ?? []} display={{ locale, ...display }} />
    </section>
  );
}

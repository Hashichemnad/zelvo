import { getTranslations, setRequestLocale } from "next-intl/server";
import { AgentChats } from "@/components/channels/AgentChats";
import { getJsonAs } from "@/lib/api";
import type { ChatQueue } from "@/lib/channels";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/chats">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "channels.agent" });
  return { title: t("title") };
}

/** Live chats of the agent's groups (task E2): waiting chats, the agent's own, and the chat open now. */
export default async function ChatsPage({ params }: PageProps<"/[locale]/chats">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/chats`);
  const t = await getTranslations({ locale, namespace: "channels.agent" });
  if (!user?.permissions.includes("chat.handle")) {
    return <p role="alert">{t("noAccess")}</p>;
  }
  const [queue, display] = await Promise.all([
    getJsonAs<ChatQueue>("/api/v1/chat/queue", session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  if (!queue) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <section aria-labelledby="chats-heading" className="space-y-6">
      <h1 id="chats-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <AgentChats
        initial={queue}
        csrfToken={session.data.csrfToken}
        canCreateTickets={user.permissions.includes("incidents.create")}
        display={{ locale, ...display }}
      />
    </section>
  );
}

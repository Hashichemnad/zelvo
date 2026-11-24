import { getTranslations, setRequestLocale } from "next-intl/server";
import { MailboxesPanel } from "@/components/channels/MailboxesPanel";
import { getJsonAs } from "@/lib/api";
import type { Mailbox } from "@/lib/channels";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/mailboxes">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "channels.mailboxes" });
  return { title: t("title") };
}

/** Settings → Mailboxes (module 6, task E2): mailboxes that turn emails into tickets. */
export default async function MailboxesPage({ params }: PageProps<"/[locale]/settings/mailboxes">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/mailboxes`);
  const t = await getTranslations({ locale, namespace: "channels.mailboxes" });
  if (!user?.permissions.includes("channels.manage")) {
    return null;
  }
  const [mailboxes, choices, display] = await Promise.all([
    getJsonAs<Mailbox[]>("/api/v1/mailboxes", session.data.accessToken, tenant),
    loadDirectoryChoices(session.data.accessToken, tenant, user.permissions),
    getDisplayFormat(),
  ]);
  if (!mailboxes) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <section aria-labelledby="mailboxes-heading" className="space-y-4">
      <h2 id="mailboxes-heading" className="text-2xl font-semibold">{t("title")}</h2>
      <MailboxesPanel initial={mailboxes} csrfToken={session.data.csrfToken} categories={choices.categories} groups={choices.groups ?? []} display={{ locale, ...display }} />
    </section>
  );
}

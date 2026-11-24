import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { MailboxDetail } from "@/components/channels/MailboxDetail";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { InboundEmail, Mailbox, OutboundEmail, Page } from "@/lib/channels";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/mailboxes/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "channels.mailboxes" });
  return { title: t("title") };
}

/** One mailbox: settings, test, sign-in and its logs (task E2). */
export default async function MailboxPage({ params, searchParams }: PageProps<"/[locale]/settings/mailboxes/[id]">) {
  const { locale, id } = await params;
  const query = await searchParams;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/mailboxes/${id}`);
  const t = await getTranslations({ locale, namespace: "channels.mailboxes" });
  if (!user?.permissions.includes("channels.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [mailbox, log, outgoing, choices, display] = await Promise.all([
    getJsonAs<Mailbox>(`/api/v1/mailboxes/${id}`, token, tenant),
    getJsonAs<Page<InboundEmail>>(`/api/v1/mailboxes/${id}/log`, token, tenant),
    getJsonAs<Page<OutboundEmail>>(`/api/v1/mailboxes/${id}/outgoing`, token, tenant),
    loadDirectoryChoices(token, tenant, user.permissions),
    getDisplayFormat(),
  ]);
  if (!mailbox || !log || !outgoing) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  const signIn = typeof query.signIn === "string" ? query.signIn : null;
  return (
    <section aria-labelledby="mailbox-heading" className="space-y-4">
      <Link href="/settings/mailboxes" className="text-sm font-semibold text-brand underline">{t("back")}</Link>
      <h2 id="mailbox-heading" className="text-2xl font-semibold">{mailbox.name}</h2>
      <p dir="ltr" className="text-ink-muted">{mailbox.address}</p>
      <MailboxDetail
        initial={mailbox}
        log={log}
        outgoing={outgoing}
        csrfToken={session.data.csrfToken}
        categories={choices.categories}
        groups={choices.groups ?? []}
        display={{ locale, ...display }}
        signIn={signIn}
      />
    </section>
  );
}

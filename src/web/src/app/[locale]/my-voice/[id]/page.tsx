import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { RecordConfirmation, RecordConversation, ReopenInquiry, TicketHappiness, VoiceFacts, VoiceProgress } from "@/components/voice/VoiceViews";
import { Link } from "@/i18n/navigation";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";
import { loadVoiceRecord } from "@/lib/voice-pages";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-voice/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "voice.mine" });
  return { title: t("title") };
}

/** One of the user's complaints, inquiries or suggestions: progress, answer, conversation, confirmation and the happiness meter. */
export default async function MyVoiceRecordPage({ params }: PageProps<"/[locale]/my-voice/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-voice/${id}`);
  const t = await getTranslations({ locale, namespace: "voice.mine" });
  const { record, comments, settings, incidentSettings, form, display } = await loadVoiceRecord(id, session.data.accessToken, tenant, locale);
  if (!record) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/my-voice" className="font-semibold text-brand underline">{t("back")}</Link>
      </section>
    );
  }
  const csrf = session.data.csrfToken;
  return (
    <article aria-labelledby="voice-heading" className="space-y-8">
      <Link href="/my-voice" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{record.number}</p>
        <h1 id="voice-heading" className="text-3xl font-bold text-brand" dir="auto">{record.title}</h1>
      </header>
      <VoiceProgress record={record} />
      {record.canConfirm && <RecordConfirmation recordId={record.id} etag={record.etag} csrfToken={csrf} />}
      <VoiceFacts record={record} display={display} settings={settings} incidentSettings={incidentSettings} staff={false} />
      {settings?.happiness.enabled && settings.happiness.askOnClosure && (record.canRate || record.rating) && (
        <TicketHappiness ticketId={record.id} settings={settings.happiness} rating={record.rating} csrfToken={csrf} />
      )}
      {form && <FormAnswersView view={form} attachmentBase={`/bff/api/v1/customer-voice/${record.id}/attachments`} />}
      <RecordConversation recordId={record.id} comments={comments} display={display} staff={false} closed={record.state === "Closed"} csrfToken={csrf} />
      {record.nextStates.includes("Reopened") && <ReopenInquiry recordId={record.id} etag={record.etag} csrfToken={csrf} />}
    </article>
  );
}

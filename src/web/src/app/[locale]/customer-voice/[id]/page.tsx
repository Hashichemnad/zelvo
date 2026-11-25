import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { SlaClocks } from "@/components/sla/SlaClocks";
import { RecordConversation, VoiceFacts, VoiceProgress } from "@/components/voice/VoiceViews";
import { VoiceWorkPanel } from "@/components/voice/VoiceWorkPanel";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { CatalogBrowse } from "@/lib/catalog";
import type { GroupSummary } from "@/lib/directory";
import { isId } from "@/lib/incident-pages";
import type { Person } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";
import { faceOf } from "@/lib/voice";
import { loadVoiceRecord } from "@/lib/voice-pages";

export async function generateMetadata({ params }: PageProps<"/[locale]/customer-voice/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "voice.staff" });
  return { title: t("title") };
}

/** One complaint, inquiry or suggestion for staff: progress, facts, SLA clocks, answers, conversation and the work panel. */
export default async function CustomerVoiceRecordPage({ params }: PageProps<"/[locale]/customer-voice/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/customer-voice/${id}`);
  const t = await getTranslations({ locale, namespace: "voice.staff" });
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("voice.read")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [data, assignees, groups, catalog] = await Promise.all([
    loadVoiceRecord(id, token, tenant, locale),
    permissions.includes("voice.assign") ? getJsonAs<Person[]>("/api/v1/customer-voice/assignees", token, tenant) : Promise.resolve(null),
    permissions.includes("directory.read") ? getJsonAs<GroupSummary[]>("/api/v1/groups", token, tenant) : Promise.resolve(null),
    getJsonAs<CatalogBrowse>("/api/v1/catalog/published?topLevel=false", token, tenant),
  ]);
  const { record, comments, settings, incidentSettings, form, display } = data;
  if (!record) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/customer-voice" className="font-semibold text-brand underline">{t("back")}</Link>
      </section>
    );
  }
  const services = (catalog?.services ?? []).map((s) => ({ id: s.id, name: s.name }));
  return (
    <article aria-labelledby="voice-heading" className="space-y-8">
      <Link href="/customer-voice" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{record.number}</p>
        <h1 id="voice-heading" className="text-3xl font-bold text-brand" dir="auto">{record.title}</h1>
      </header>
      <VoiceProgress record={record} />
      <VoiceFacts record={record} display={display} settings={settings} incidentSettings={incidentSettings} staff />
      {record.rating && settings && (
        <p data-testid="voice-rating">
          <span className="font-semibold">{t("rating")}: </span>
          <span aria-hidden="true">{faceOf(record.rating.score, record.rating.points)} </span>
          {t("score", { score: record.rating.score, points: record.rating.points })}
          {record.rating.comment && <span className="ms-2" dir="auto">“{record.rating.comment}”</span>}
        </p>
      )}
      {record.sla.length > 0 && <SlaClocks clocks={record.sla} display={display} />}
      {form && <FormAnswersView view={form} attachmentBase={`/bff/api/v1/customer-voice/${record.id}/attachments`} />}
      <RecordConversation recordId={record.id} comments={comments} display={display} staff={permissions.includes("voice.edit")} closed={record.state === "Closed"} csrfToken={session.data.csrfToken} />
      {(permissions.includes("voice.edit") || permissions.includes("voice.assign")) && (
        <VoiceWorkPanel record={record} settings={settings} incidentSettings={incidentSettings} groups={groups ?? []} assignees={assignees}
          services={services} permissions={permissions} csrfToken={session.data.csrfToken} />
      )}
    </article>
  );
}

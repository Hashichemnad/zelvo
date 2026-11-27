import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { Localized } from "@/components/directory/common";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { RecordConversation } from "@/components/records/RecordConversation";
import { RecordWorkPanel } from "@/components/records/RecordWorkPanel";
import { SlaClocks } from "@/components/sla/SlaClocks";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { formatDateTime } from "@/lib/format";
import type { TicketFormView } from "@/lib/forms/types";
import { isId } from "@/lib/incident-pages";
import { inLanguage, type IncidentSettings, type Locale } from "@/lib/incidents";
import type { CustomRecord, RecordComment, RecordType } from "@/lib/records";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/records/[type]/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "records.index" });
  return { title: t("title") };
}

/**
 * One record of a custom type (task K2): staff see its facts, clocks, the work panel and internal
 * notes; the requester sees their own record with the team that has it, never the agent.
 */
export default async function RecordPage({ params }: PageProps<"/[locale]/records/[type]/[id]">) {
  const { locale, type: key, id } = await params;
  setRequestLocale(locale);
  if (!/^[a-z][a-z0-9-]{0,39}$/.test(key) || !isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/records/${key}/${id}`);
  const t = await getTranslations({ locale, namespace: "records.detail" });
  const token = session.data.accessToken;
  const get = <T,>(path: string) => getJsonAs<T>(path, token, tenant);
  const [type, record, comments, form, incidentSettings, choices, format] = await Promise.all([
    get<RecordType>(`/api/v1/record-types/${key}`),
    get<CustomRecord>(`/api/v1/records/${key}/${id}`),
    get<RecordComment[]>(`/api/v1/records/${key}/${id}/comments`),
    get<TicketFormView>(`/api/v1/records/${key}/${id}/form`),
    get<IncidentSettings>("/api/v1/settings/incidents"),
    loadDirectoryChoices(token, tenant, user?.permissions ?? []),
    getDisplayFormat(),
  ]);
  if (!type || !record) notFound();
  const display = { locale, ...format };
  const l = locale as Locale;
  const staff = type.access.read;
  const mine = record.requester?.id === user?.id;
  const typeName = inLanguage(type.name, l);
  const row = (label: string, value: React.ReactNode) => (
    <div className="flex flex-wrap gap-x-2"><dt className="font-semibold">{label}:</dt><dd>{value}</dd></div>
  );

  return (
    <article aria-labelledby="record-heading" className="space-y-6">
      <Link href={`/records/${key}`} className="text-sm font-semibold text-brand underline-offset-4 hover:underline">
        <span lang={typeName.lang} dir={typeName.dir}>{typeName.value}</span>
      </Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{record.number}</p>
        <h1 id="record-heading" className="text-3xl font-bold text-brand" dir="auto">{record.title}</h1>
      </header>
      <section aria-labelledby="record-facts" className="space-y-3 rounded-lg border border-line p-4" data-testid="record-facts">
        <h2 id="record-facts" className="text-lg font-semibold">{t("facts")}</h2>
        <dl className="grid gap-2 sm:grid-cols-2">
          {row(t("state"), <Localized text={record.stateOfType.name} locale={l} />)}
          {row(t("team"), record.group ? <Localized text={record.group.name} locale={l} /> : t("none"))}
          {staff && row(t("assignee"), record.assignee?.displayName ?? t("nobody"))}
          {staff && row(t("requester"), record.requester?.displayName ?? "—")}
          {staff && row(t("priority"), <span dir="ltr">{`P${record.priority}`}</span>)}
          {row(t("category"), record.category ? <Localized text={record.category.path} locale={l} /> : t("none"))}
          {row(t("created"), formatDateTime(new Date(record.createdAt), display))}
        </dl>
        {record.awaitingApproval && <p className="font-semibold">{t("awaitingApproval")}</p>}
        {record.description && <p className="whitespace-pre-wrap" dir="auto">{record.description}</p>}
        {record.resolutionNotes && (
          <div>
            <h3 className="font-semibold">{t("resolution")}</h3>
            <p className="whitespace-pre-wrap" dir="auto">{record.resolutionNotes}</p>
          </div>
        )}
      </section>
      {form?.form && <FormAnswersView view={form} attachmentBase={`/bff/api/v1/records/${key}/${id}/attachments`} />}
      {staff && record.sla.length > 0 && <SlaClocks clocks={record.sla} display={display} />}
      {(type.access.edit || type.access.assign || (mine && record.nextStates.length > 0)) && (
        <RecordWorkPanel key={record.etag} record={record} reasons={incidentSettings?.onHoldReasons ?? []} groups={choices.groups ?? []}
          canAssign={type.access.assign} csrfToken={session.data.csrfToken} />
      )}
      <RecordConversation recordType={key} recordId={record.id} initial={comments ?? []} staff={type.access.edit}
        canWrite={record.state !== "Closed" && (type.access.edit || mine)} csrfToken={session.data.csrfToken} display={display} />
    </article>
  );
}

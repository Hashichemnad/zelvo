import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { AttachmentsPanel } from "@/components/incidents/AttachmentsPanel";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { IncidentFacts } from "@/components/incidents/IncidentFacts";
import { SlaClocks } from "@/components/sla/SlaClocks";
import { IncidentHistory } from "@/components/incidents/IncidentHistory";
import { IncidentWorkPanel } from "@/components/incidents/IncidentWorkPanel";
import { MajorTimeline } from "@/components/incidents/MajorTimeline";
import { TicketCiPanel } from "@/components/cmdb/TicketCiPanel";
import { CreateArticleFromIncident } from "@/components/knowledge/NewArticle";
import { ExportPdfButton } from "@/components/ExportPdfButton";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { isId, loadAssignees, loadIncident } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/incidents/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "incidents" });
  return { title: t("title") };
}

/** One incident for staff: facts, work actions, attachments, the major-incident timeline and history. */
export default async function IncidentPage({ params }: PageProps<"/[locale]/incidents/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/incidents/${id}`);
  const t = await getTranslations({ locale, namespace: "incidents" });
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("incidents.read")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [data, assignees, choices, aiStatus] = await Promise.all([
    loadIncident(id, token, tenant, locale, { staff: true }),
    permissions.includes("incidents.assign") ? loadAssignees(token, tenant) : Promise.resolve(null),
    loadDirectoryChoices(token, tenant, permissions),
    getJsonAs<{ features: { feature: string; available: boolean }[] }>("/api/v1/ai/status", token, tenant),
  ]);
  const articleDraftsOn = aiStatus?.features.some((f) => f.feature === "articleDrafts" && f.available) ?? false;
  const directory = { categories: choices.categories, locations: choices.locations, groups: choices.groups ?? [] };
  const { incident, settings, attachments, attachmentSettings, updates, history, display, form } = data;
  if (!incident) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/incidents" className="font-semibold text-brand underline">
          {t("backToList")}
        </Link>
      </section>
    );
  }
  const closed = incident.state === "Closed";

  return (
    <article aria-labelledby="incident-heading" className="space-y-8">
      <Link href="/incidents" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">
        {t("backToList")}
      </Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">
          {incident.number}
        </p>
        <h1 id="incident-heading" className="text-3xl font-bold text-brand" dir="auto">
          {incident.title}
        </h1>
      </header>
      {/* Task F1: a branded PDF in the page's language. */}
      <ExportPdfButton path={`/incidents/${incident.id}`} csrfToken={session.data.csrfToken} />
      <IncidentFacts incident={incident} settings={settings} display={display} basePath="/incidents" />
      {form && <FormAnswersView view={form} attachmentBase={`/bff/api/v1/incidents/${incident.id}/attachments`} />}
      {incident.sla && <SlaClocks clocks={incident.sla} display={display} />}
      <IncidentWorkPanel incident={incident} settings={settings} csrfToken={session.data.csrfToken} permissions={permissions} assignees={assignees} directory={directory} />
      {/* Task G1: the configuration items the incident is about. */}
      <TicketCiPanel ticketId={incident.id} kind="incidents" canEdit={permissions.includes("incidents.edit")} canOpenItems={permissions.includes("cmdb.read")}
        csrfToken={session.data.csrfToken} />
      {(incident.state === "Resolved" || closed) && permissions.includes("knowledge.write") && (
        <CreateArticleFromIncident incidentId={incident.id} csrfToken={session.data.csrfToken} withAi={articleDraftsOn} />
      )}
      {(incident.isMajor || updates.length > 0) && (
        <MajorTimeline
          incidentId={incident.id}
          updates={updates}
          canPost={incident.isMajor && !closed && permissions.includes("incidents.major")}
          staff
          csrfToken={session.data.csrfToken}
          display={display}
        />
      )}
      <AttachmentsPanel
        incidentId={incident.id}
        attachments={attachments}
        csrfToken={session.data.csrfToken}
        canUpload={!closed && permissions.includes("incidents.edit")}
        limits={attachmentSettings}
        display={display}
      />
      <IncidentHistory entries={history} settings={settings} display={display} />
    </article>
  );
}

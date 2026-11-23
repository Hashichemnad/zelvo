import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { AttachmentsPanel } from "@/components/incidents/AttachmentsPanel";
import { IncidentFacts } from "@/components/incidents/IncidentFacts";
import { IncidentHistory } from "@/components/incidents/IncidentHistory";
import { IncidentWorkPanel } from "@/components/incidents/IncidentWorkPanel";
import { MajorTimeline } from "@/components/incidents/MajorTimeline";
import { Link } from "@/i18n/navigation";
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
  const [data, assignees] = await Promise.all([
    loadIncident(id, token, tenant, locale, { staff: true }),
    permissions.includes("incidents.assign") ? loadAssignees(token, tenant) : Promise.resolve(null),
  ]);
  const { incident, settings, attachments, attachmentSettings, updates, history, display } = data;
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
      <IncidentFacts incident={incident} settings={settings} display={display} basePath="/incidents" />
      <IncidentWorkPanel incident={incident} settings={settings} csrfToken={session.data.csrfToken} permissions={permissions} assignees={assignees} />
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

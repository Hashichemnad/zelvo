import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { AttachmentsPanel } from "@/components/incidents/AttachmentsPanel";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { IncidentFacts } from "@/components/incidents/IncidentFacts";
import { MajorTimeline } from "@/components/incidents/MajorTimeline";
import { ReopenButton } from "@/components/incidents/ReopenButton";
import { TicketRatingBlock } from "@/components/voice/TicketRatingBlock";
import { Composer, ConversationList } from "@/components/workspace/Conversation";
import { ExportPdfButton } from "@/components/ExportPdfButton";
import { Link } from "@/i18n/navigation";
import { getAppInfo, getJsonAs } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { RememberTicket } from "@/components/pwa/RememberTicket";
import { isId, loadIncident } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";
import type { Comment } from "@/lib/workspace";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-incidents/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "incidents" });
  return { title: t("myTitle") };
}

/**
 * One of the requester's incidents: progress, the conversation with staff (public replies, task B4),
 * updates meant for them, their files, reopen or follow up.
 */
export default async function MyIncidentPage({ params, searchParams }: PageProps<"/[locale]/my-incidents/[id]">) {
  const { locale, id } = await params;
  // Task J1: files added with the report that the upload refused (type, size).
  const filesRefused = Math.min(10, Number((await searchParams).filesRefused) || 0);
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-incidents/${id}`);
  const [t, tConversation] = await Promise.all([
    getTranslations({ locale, namespace: "incidents" }),
    getTranslations({ locale, namespace: "workspace.conversation" }),
  ]);
  const [{ incident, settings, attachments, attachmentSettings, updates, display, form }, comments, aiStatus] = await Promise.all([
    loadIncident(id, session.data.accessToken, tenant, locale, { staff: false }),
    getJsonAs<Comment[]>(`/api/v1/incidents/${encodeURIComponent(id)}/comments`, session.data.accessToken, tenant),
    // Task E5: requesters translate staff replies with one click when the AI feature is on (LANG-7).
    getJsonAs<{ features: { feature: string; available: boolean }[] }>("/api/v1/ai/status", session.data.accessToken, tenant),
  ]);
  const canTranslate = aiStatus?.features.some((f) => f.feature === "translation" && f.available) ?? false;
  if (!incident) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/my-incidents" className="font-semibold text-brand underline">
          {t("backToMine")}
        </Link>
      </section>
    );
  }
  const closed = incident.state === "Closed";
  // Task J1: kept on the device for the offline page, as the requester sees it now.
  const app = await getAppInfo(tenant);
  const when = (iso: string) => formatDateTime(new Date(iso), display);
  const offline = {
    id: incident.id,
    number: incident.number,
    title: incident.title ?? incident.number,
    state: t(`states.${incident.state}`),
    team: null,
    description: incident.description ?? null,
    href: `/${locale}/my-incidents/${incident.id}`,
    messages: (comments ?? []).filter((c) => c.visibility === "Public").slice(-20)
      .map((c) => ({ author: c.author.displayName ?? "", text: c.body, at: when(c.createdAt) })),
    updated: when(incident.updatedAt),
  };

  return (
    <article aria-labelledby="incident-heading" className="space-y-8">
      <RememberTicket ticket={offline} enabled={app?.offlineData ?? true} />
      <Link href="/my-incidents" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">
        {t("backToMine")}
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
      {filesRefused > 0 && <p role="alert" className="rounded-md border border-line bg-surface-muted p-3">{t("attachments.someRefused", { count: filesRefused })}</p>}
      <IncidentFacts incident={incident} settings={settings} display={display} basePath="/my-incidents" />
      {form && <FormAnswersView view={form} attachmentBase={`/bff/api/v1/incidents/${incident.id}/attachments`} />}
      {(incident.state === "Resolved" || closed) && (
        <TicketRatingBlock ticketId={incident.id} accessToken={session.data.accessToken} tenant={tenant} csrfToken={session.data.csrfToken} />
      )}
      {incident.canReopen && <ReopenButton incidentId={incident.id} csrfToken={session.data.csrfToken} />}
      {closed && (
        <div className="space-y-2 rounded-lg border border-line p-4">
          <p>{t("closedNote")}</p>
          <Link href={`/my-incidents/new?followUpOf=${incident.id}`} className="font-semibold text-brand underline">
            {t("followUp")}
          </Link>
        </div>
      )}
      <section aria-labelledby="conversation-heading" className="space-y-3">
        <h2 id="conversation-heading" className="text-xl font-bold">{tConversation("title")}</h2>
        {comments ? (
          <ConversationList comments={comments} display={display} staff={false}
            translate={canTranslate ? { incidentId: incident.id, csrfToken: session.data.csrfToken } : null} />
        ) : <p role="alert">{tConversation("unavailable")}</p>}
        {closed ? (
          <p className="text-ink-muted">{tConversation("closed")}</p>
        ) : (
          <Composer incidentId={incident.id} csrfToken={session.data.csrfToken} staff={false} responses={[]} people={[]} ticketNumber={incident.number} />
        )}
      </section>
      {updates.length > 0 && (
        <MajorTimeline incidentId={incident.id} updates={updates} canPost={false} staff={false} csrfToken={session.data.csrfToken} display={display} />
      )}
      <AttachmentsPanel
        incidentId={incident.id}
        attachments={attachments}
        csrfToken={session.data.csrfToken}
        canUpload={!closed}
        limits={attachmentSettings}
        display={display}
      />
    </article>
  );
}

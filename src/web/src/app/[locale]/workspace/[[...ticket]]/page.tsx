import { getTranslations, setRequestLocale } from "next-intl/server";
import { AttachmentsPanel } from "@/components/incidents/AttachmentsPanel";
import { IncidentFacts } from "@/components/incidents/IncidentFacts";
import { IncidentHistory } from "@/components/incidents/IncidentHistory";
import { IncidentWorkPanel } from "@/components/incidents/IncidentWorkPanel";
import { MajorTimeline } from "@/components/incidents/MajorTimeline";
import { Composer, ConversationList } from "@/components/workspace/Conversation";
import { TicketCiPanel } from "@/components/cmdb/TicketCiPanel";
import { AiAssistantPlaceholder, CustomerPanel } from "@/components/workspace/CustomerPanel";
import { AiAssistantPanel } from "@/components/ai/AiAssistantPanel";
import type { AiInsights } from "@/lib/ai-features";
import { LiveSlaTimers } from "@/components/workspace/SlaCountdown";
import { WorkspaceShell } from "@/components/workspace/WorkspaceShell";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { isId, loadAssignees, loadIncident } from "@/lib/incident-pages";
import type { IncidentPage, IncidentSettings } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";
import { builtInViews, filtersFrom, filtersQuery, type CannedResponse, type Comment, type Customer, type SavedView } from "@/lib/workspace";

export async function generateMetadata({ params }: PageProps<"/[locale]/workspace/[[...ticket]]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "workspace" });
  return { title: t("title") };
}

/**
 * The agent workspace (module 5, task B4): one screen with the incident list, the open incident
 * and its conversation, and the customer beside it. Everything loads through the public API; the
 * browser moves between incidents and views without reloading the page.
 */
export default async function WorkspacePage({ params, searchParams }: PageProps<"/[locale]/workspace/[[...ticket]]">) {
  const { locale, ticket } = await params;
  setRequestLocale(locale);
  const selectedId = ticket?.[0] && isId(ticket[0]) && ticket.length === 1 ? ticket[0] : null;
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/workspace${selectedId ? `/${selectedId}` : ""}`);
  const t = await getTranslations({ locale, namespace: "workspace" });
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("incidents.read")) {
    return (
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>
      </section>
    );
  }

  const raw = await searchParams;
  const asked = filtersFrom(raw);
  const filters = Object.keys(asked).length > 0 ? asked : builtInViews[0]!.filters;
  const pageNumber = Math.max(1, Math.min(100_000, Number(typeof raw.page === "string" ? raw.page : "1") || 1));
  const token = session.data.accessToken;
  const get = <T,>(path: string) => getJsonAs<T>(path, token, tenant);
  const mayEdit = permissions.includes("incidents.edit");

  const [page, settings, views, choices, assignees, responses, format] = await Promise.all([
    get<IncidentPage>(`/api/v1/incidents?${filtersQuery(filters, pageNumber)}`),
    get<IncidentSettings>("/api/v1/settings/incidents"),
    get<SavedView[]>("/api/v1/workspace/views"),
    loadDirectoryChoices(token, tenant, permissions),
    permissions.includes("incidents.assign") ? loadAssignees(token, tenant) : Promise.resolve(null),
    mayEdit ? get<CannedResponse[]>("/api/v1/canned-responses") : Promise.resolve(null),
    getDisplayFormat(),
  ]);
  const display = { locale, ...format };
  const groups = choices.groups ?? [];

  let detail: React.ReactNode = <p className="rounded-lg border border-dashed border-line p-6 text-ink-muted">{t("detail.none")}</p>;
  let aside: React.ReactNode = <AiAssistantPlaceholder />;
  if (selectedId) {
    const [data, comments, customer, insights] = await Promise.all([
      loadIncident(selectedId, token, tenant, locale, { staff: true }),
      get<Comment[]>(`/api/v1/incidents/${selectedId}/comments`),
      get<Customer>(`/api/v1/incidents/${selectedId}/customer`),
      // Task E5: what the AI found and which AI features can be used now.
      get<AiInsights>(`/api/v1/incidents/${selectedId}/ai`),
    ]);
    const available = new Set(insights?.available ?? []);
    const { incident, attachments, attachmentSettings, updates, history } = data;
    if (!incident) {
      detail = <p role="alert">{t("detail.notFound")}</p>;
    } else {
      const closed = incident.state === "Closed";
      const directory = { categories: choices.categories, locations: choices.locations, groups };
      detail = (
        <article aria-labelledby="ticket-heading" className="space-y-5" data-testid="workspace-detail">
          <header className="space-y-2">
            <p className="flex flex-wrap items-center gap-3 text-sm">
              <span className="font-mono text-ink-muted" dir="ltr">{incident.number}</span>
              <Link href={`/incidents/${incident.id}`} className="font-semibold text-brand underline-offset-4 hover:underline">{t("detail.fullPage")}</Link>
            </p>
            <h2 id="ticket-heading" className="text-2xl font-bold text-brand" dir="auto">{incident.title}</h2>
            {incident.sla && <LiveSlaTimers clocks={incident.sla} display={display} />}
            {incident.description && <p className="whitespace-pre-wrap" dir="auto">{incident.description}</p>}
          </header>
          <IncidentFacts incident={incident} settings={settings} display={display} basePath="/workspace" />
          <section aria-labelledby="conversation-heading" className="space-y-3">
            <h2 id="conversation-heading" className="text-xl font-bold">{t("conversation.title")}</h2>
            {comments ? (
              <ConversationList comments={comments} display={display} staff
                translate={available.has("translation") ? { incidentId: incident.id, csrfToken: session.data.csrfToken } : null} />
            ) : <p role="alert">{t("conversation.unavailable")}</p>}
            {mayEdit && !closed && (
              <Composer
                incidentId={incident.id}
                csrfToken={session.data.csrfToken}
                staff
                requesterLanguage={customer?.preferredLanguage ?? null}
                responses={responses ?? []}
                people={assignees ?? []}
                ai={{ reply: available.has("reply"), translation: available.has("translation") }}
              />
            )}
            {closed && <p className="text-ink-muted">{t("conversation.closed")}</p>}
          </section>
          <div id="workspace-work">
            <IncidentWorkPanel incident={incident} settings={settings} csrfToken={session.data.csrfToken} permissions={permissions} assignees={assignees} directory={directory} />
          </div>
          <TicketCiPanel key={incident.id} ticketId={incident.id} kind="incidents" canEdit={mayEdit} canOpenItems={permissions.includes("cmdb.read")} canScan={permissions.includes("assets.read")}
            csrfToken={session.data.csrfToken} />
          {(incident.isMajor || updates.length > 0) && (
            <MajorTimeline incidentId={incident.id} updates={updates} canPost={incident.isMajor && !closed && permissions.includes("incidents.major")}
              staff csrfToken={session.data.csrfToken} display={display} />
          )}
          <AttachmentsPanel incidentId={incident.id} attachments={attachments} csrfToken={session.data.csrfToken}
            canUpload={!closed && mayEdit} limits={attachmentSettings} display={display} />
          <details>
            <summary className="cursor-pointer font-semibold">{t("detail.history")}</summary>
            <IncidentHistory entries={history} settings={settings} display={display} />
          </details>
        </article>
      );
      aside = (
        <>
          <CustomerPanel customer={customer} display={display} />
          <AiAssistantPanel incidentId={incident.id} insights={insights} csrfToken={session.data.csrfToken} permissions={permissions}
            settings={settings} categories={choices.categories} groups={groups} display={display} />
        </>
      );
    }
  }

  return (
    <WorkspaceShell
      page={page}
      filters={filters}
      views={views ?? []}
      selectedId={selectedId}
      settings={settings}
      groups={groups}
      categories={choices.categories}
      locations={choices.locations}
      assignees={assignees}
      permissions={permissions}
      csrfToken={session.data.csrfToken}
      display={display}
      detail={detail}
      aside={aside}
    />
  );
}

import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { ChangePlan, RiskQuestionnaire } from "@/components/changes/ChangePlan";
import { ChangeConflicts, ChangeLinks, ChangeNotes, ChangeTasks } from "@/components/changes/ChangeRelated";
import { ChangeWorkPanel } from "@/components/changes/ChangeWork";
import { ChangeFacts } from "@/components/changes/ChangeFacts";
import { TicketCiPanel } from "@/components/cmdb/TicketCiPanel";
import { RequestApprovals } from "@/components/workflows/Approvals";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { RequestTask } from "@/lib/catalog";
import type { Change, ChangeComment, ChangeLink, ChangeSettings } from "@/lib/changes";
import { getDisplayFormat } from "@/lib/display";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { isId } from "@/lib/incident-pages";
import type { ApprovalProcessView } from "@/lib/workflows";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/changes/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "changes.list" });
  return { title: t("title") };
}

/**
 * One change (task H3): its facts, the work panel (submit, implement, review, assignment), the
 * plans and window, the risk questionnaire, conflicts, its approval, configuration items, tasks,
 * links to problems and incidents, and notes.
 */
export default async function ChangePage({ params }: PageProps<"/[locale]/changes/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/changes/${id}`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "changes.detail" }), getTranslations({ locale, namespace: "changes.list" })]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("changes.read")) {
    return <p role="alert">{user ? tList("noAccess") : tList("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const get = <T,>(path: string) => getJsonAs<T>(path, token, tenant);
  const [change, tasks, links, notes, approvals, settings, choices, format] = await Promise.all([
    get<Change>(`/api/v1/changes/${id}`),
    get<RequestTask[]>(`/api/v1/changes/${id}/tasks`),
    get<ChangeLink[]>(`/api/v1/changes/${id}/links`),
    get<ChangeComment[]>(`/api/v1/changes/${id}/comments`),
    get<ApprovalProcessView[]>(`/api/v1/changes/${id}/approvals`),
    get<ChangeSettings>("/api/v1/settings/changes"),
    loadDirectoryChoices(token, tenant, permissions),
    getDisplayFormat(),
  ]);
  if (!change) notFound();
  const display = { locale, ...format };
  const csrfToken = session.data.csrfToken;
  const canEdit = permissions.includes("changes.edit") && change.state !== "Closed";
  const canManage = permissions.includes("changes.manage");

  return (
    <article aria-labelledby="change-heading" className="space-y-6">
      <Link href="/changes" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{change.number}</p>
        <h1 id="change-heading" className="text-3xl font-bold text-brand" dir="auto">{change.title}</h1>
      </header>
      <ChangeFacts change={change} display={display} />
      <ChangeWorkPanel key={`work-${change.etag}`} change={change} groups={choices.groups ?? []} canAssign={canManage} csrfToken={csrfToken} />
      <ChangeConflicts conflicts={change.conflicts ?? []} display={display} />
      <ChangePlan key={`plan-${change.etag}`} change={change} canEdit={canEdit} csrfToken={csrfToken} display={display} />
      {settings && <RiskQuestionnaire key={`risk-${change.etag}`} change={change} settings={settings} canEdit={canEdit} csrfToken={csrfToken} />}
      {approvals && approvals.length > 0 && (
        <section aria-labelledby="change-approvals-heading" className="space-y-3 rounded-lg border border-line p-4">
          <h2 id="change-approvals-heading" className="text-lg font-semibold">{t("approvals")}</h2>
          <RequestApprovals processes={approvals} display={display} />
        </section>
      )}
      <TicketCiPanel ticketId={change.id} kind="changes" canEdit={canEdit && change.stage === "New"} canOpenItems={permissions.includes("cmdb.read")} csrfToken={csrfToken} />
      <ChangeTasks changeId={change.id} initial={tasks ?? []} groups={choices.groups ?? []} canEdit={canEdit && change.stage !== "Review"} csrfToken={csrfToken} display={display} />
      <ChangeLinks changeId={change.id} initial={links ?? []} canEdit={canEdit} csrfToken={csrfToken} />
      <ChangeNotes changeId={change.id} initial={notes ?? []} canWrite={canEdit} csrfToken={csrfToken} display={display} />
    </article>
  );
}

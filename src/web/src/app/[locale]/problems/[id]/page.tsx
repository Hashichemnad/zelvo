import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { TicketCiPanel } from "@/components/cmdb/TicketCiPanel";
import { SlaClocks } from "@/components/sla/SlaClocks";
import { KnownErrorEditor } from "@/components/problems/KnownErrorEditor";
import { ProblemAnalysisEditor } from "@/components/problems/ProblemAnalysisEditor";
import { ProblemFacts } from "@/components/problems/ProblemFacts";
import { ProblemArticles, ProblemIncidents } from "@/components/problems/ProblemLinks";
import { ProblemNotes, ProblemTimeline } from "@/components/problems/ProblemNotes";
import { ProblemWorkPanel } from "@/components/problems/ProblemWorkPanel";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { isId } from "@/lib/incident-pages";
import { inLanguage, type IncidentSettings, type Locale } from "@/lib/incidents";
import type { Problem, ProblemArticle, ProblemComment, ProblemIncident, ProblemSettings, TimelineEntry } from "@/lib/problems";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/problems/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "problems.list" });
  return { title: t("title") };
}

/**
 * One problem (task H1): details, the work panel (stages, assignment), the root-cause tools, the
 * known error, linked incidents, configuration items and articles, the timeline and notes.
 */
export default async function ProblemPage({ params }: PageProps<"/[locale]/problems/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/problems/${id}`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "problems.detail" }), getTranslations({ locale, namespace: "problems.list" })]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("problems.read")) {
    return <p role="alert">{user ? tList("noAccess") : tList("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const get = <T,>(path: string) => getJsonAs<T>(path, token, tenant);
  const [problem, incidents, articles, notes, timeline, settings, incidentSettings, choices, format] = await Promise.all([
    get<Problem>(`/api/v1/problems/${id}`),
    get<ProblemIncident[]>(`/api/v1/problems/${id}/incidents`),
    get<ProblemArticle[]>(`/api/v1/problems/${id}/articles`),
    get<ProblemComment[]>(`/api/v1/problems/${id}/comments`),
    get<TimelineEntry[]>(`/api/v1/problems/${id}/timeline`),
    get<ProblemSettings>("/api/v1/settings/problems"),
    get<IncidentSettings>("/api/v1/settings/incidents"),
    loadDirectoryChoices(token, tenant, permissions),
    getDisplayFormat(),
  ]);
  if (!problem) notFound();
  const display = { locale, ...format };
  const csrfToken = session.data.csrfToken;
  const canEdit = permissions.includes("problems.edit") && problem.state !== "Closed";
  const canManage = permissions.includes("problems.manage");
  const reason = incidentSettings?.onHoldReasons.find((r) => r.key === problem.onHoldReason);

  return (
    <article aria-labelledby="problem-heading" className="space-y-6">
      <Link href="/problems" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{problem.number}</p>
        <h1 id="problem-heading" className="text-3xl font-bold text-brand" dir="auto">{problem.title}</h1>
      </header>
      <ProblemFacts key={problem.etag} problem={problem} categories={choices.categories} reasonName={reason ? inLanguage(reason.name, locale as Locale).value : null}
        canEdit={canEdit} csrfToken={csrfToken} display={display} />
      {problem.sla.length > 0 && <SlaClocks clocks={problem.sla} display={display} />}
      <ProblemWorkPanel key={`work-${problem.etag}`} problem={problem} reasons={incidentSettings?.onHoldReasons ?? []} groups={choices.groups ?? []}
        canAssign={canManage} csrfToken={csrfToken} />
      <ProblemAnalysisEditor key={`analysis-${problem.etag}`} problemId={problem.id} etag={problem.etag}
        fiveWhys={problem.fiveWhys ?? { statement: null, steps: [], conclusion: null }} fishbone={problem.fishbone ?? { effect: null, causes: [] }}
        categories={settings?.fishboneCategories ?? []} canEdit={canEdit} csrfToken={csrfToken} />
      <KnownErrorEditor key={`ke-${problem.knownError?.etag ?? "none"}`} problemId={problem.id} knownError={problem.knownError} canEdit={canEdit} canManage={canManage}
        canWriteArticles={permissions.includes("knowledge.write")} csrfToken={csrfToken} />
      <ProblemIncidents problemId={problem.id} initial={incidents ?? []} canEdit={canEdit} csrfToken={csrfToken} display={display} />
      <TicketCiPanel ticketId={problem.id} kind="problems" canEdit={canEdit} canOpenItems={permissions.includes("cmdb.read")} csrfToken={csrfToken} />
      <ProblemArticles problemId={problem.id} initial={articles ?? []} canEdit={canEdit} csrfToken={csrfToken} />
      <ProblemTimeline entries={timeline ?? []} display={display} />
      <ProblemNotes problemId={problem.id} initial={notes ?? []} canWrite={canEdit} csrfToken={csrfToken} display={display} />
    </article>
  );
}

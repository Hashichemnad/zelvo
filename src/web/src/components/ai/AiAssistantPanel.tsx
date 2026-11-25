"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { ApiError, postJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type IncidentSettings, type Locale, type NamedLevel } from "@/lib/incidents";
import type { GroupSummary, TreeNode } from "@/lib/directory";
import { aiErrorKey, type AiInsights, type AiSummary, type DuplicateCandidate, type TriageSuggestion } from "@/lib/ai-features";
import { Localized } from "@/components/incidents/StateBadge";
import { inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";

const primary = "rounded-md bg-brand px-3 py-1.5 text-sm font-semibold text-surface disabled:opacity-60";

/**
 * The AI assistant beside the open incident (module 9, task E5): the triage suggestion to accept,
 * correct or dismiss; the requester's sentiment (frustrated requesters alert the group's team
 * leads); possible duplicates to link or dismiss; and a summary of the thread in the agent's
 * language. The AI never changes the incident on its own: agents decide, except triage when the
 * administrator turned on "apply automatically".
 */
export function AiAssistantPanel({
  incidentId,
  insights,
  csrfToken,
  permissions,
  settings,
  categories,
  groups,
  display,
}: {
  incidentId: string;
  insights: AiInsights | null;
  csrfToken: string;
  permissions: readonly string[];
  settings: IncidentSettings | null;
  categories: TreeNode[];
  groups: GroupSummary[];
  display: DateDisplayOptions;
}) {
  const t = useTranslations("ai.assist");
  const id = useId();
  const available = new Set(insights?.available ?? []);
  const mayEdit = permissions.includes("incidents.edit");
  const anything = insights && (available.size > 0 || insights.triage || insights.sentiment || insights.duplicates.length > 0 || insights.summary);
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line bg-surface p-3" data-testid="ai-assistant">
      <h2 id={`${id}-heading`} className="text-lg font-bold">{t("title")}</h2>
      {!anything ? (
        <p className="text-sm text-ink-muted">{t("off")}</p>
      ) : (
        <>
          {insights.sentiment && <Sentiment insights={insights} display={display} />}
          {insights.triage && settings && (
            <Triage incidentId={incidentId} suggestion={insights.triage} csrfToken={csrfToken} mayEdit={mayEdit} display={display}
              mayAssign={permissions.includes("incidents.assign")} settings={settings} categories={categories} groups={groups} />
          )}
          {!insights.triage && mayEdit && available.has("triage") && <TriageNow incidentId={incidentId} csrfToken={csrfToken} />}
          {insights.duplicates.length > 0 && <Duplicates incidentId={incidentId} duplicates={insights.duplicates} csrfToken={csrfToken} mayEdit={mayEdit} />}
          {(available.has("summary") || insights.summary) && (
            <Summary incidentId={incidentId} initial={insights.summary} csrfToken={csrfToken} canMake={available.has("summary")} display={display} />
          )}
        </>
      )}
      <p className="text-xs text-ink-muted">{t("disclaimer")}</p>
    </section>
  );
}

function Sentiment({ insights, display }: { insights: AiInsights; display: DateDisplayOptions }) {
  const t = useTranslations("ai.assist");
  const frustrated = insights.sentiment === "Frustrated";
  return (
    <div className={`space-y-1 rounded-md border p-2 text-sm ${frustrated ? "border-bad" : "border-line"}`} data-testid="ai-sentiment">
      <p className="font-semibold">
        {t("sentiment.label")}{" "}
        <span className={frustrated ? "text-bad" : insights.sentiment === "Negative" ? "text-warn" : ""}>{t(`sentiment.${insights.sentiment}`)}</span>
      </p>
      {frustrated && insights.frustratedSince && (
        <p>{t("sentiment.since", { time: formatDateTime(new Date(insights.frustratedSince), display) })}</p>
      )}
      {insights.sentimentReason && <p className="text-ink-muted" lang="en" dir="ltr">{insights.sentimentReason}</p>}
    </div>
  );
}

function TriageNow({ incidentId, csrfToken }: { incidentId: string; csrfToken: string }) {
  const t = useTranslations("ai.assist");
  const router = useRouter();
  const [state, setState] = useState<{ busy: boolean; error: string | null }>({ busy: false, error: null });
  async function run() {
    setState({ busy: true, error: null });
    try {
      await postJson(`/v1/incidents/${incidentId}/ai/triage`, {}, csrfToken);
      router.refresh();
      setState({ busy: false, error: null });
    } catch (error) {
      setState({ busy: false, error: t(aiErrorKey(error instanceof ApiError ? error.code : null)) });
    }
  }
  return (
    <div className="space-y-1">
      <button type="button" onClick={() => void run()} disabled={state.busy} className={secondaryButtonClass}>{state.busy ? t("working") : t("triage.run")}</button>
      {state.error && <p role="alert" className="text-sm text-bad">{state.error}</p>}
    </div>
  );
}

function Triage({
  incidentId, suggestion, csrfToken, mayEdit, mayAssign, settings, categories, groups, display,
}: {
  display: DateDisplayOptions;
  incidentId: string;
  suggestion: TriageSuggestion;
  csrfToken: string;
  mayEdit: boolean;
  mayAssign: boolean;
  settings: IncidentSettings;
  categories: TreeNode[];
  groups: GroupSummary[];
}) {
  const t = useTranslations("ai.assist.triage");
  const tAi = useTranslations("ai.assist");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [correcting, setCorrecting] = useState(false);
  const [categoryId, setCategoryId] = useState(suggestion.category?.id ?? "");
  const [impact, setImpact] = useState(String(suggestion.impact ?? ""));
  const [urgency, setUrgency] = useState(String(suggestion.urgency ?? ""));
  const [groupId, setGroupId] = useState(suggestion.group?.id ?? "");
  const [state, setState] = useState<{ busy: boolean; error: string | null }>({ busy: false, error: null });
  const level = (list: NamedLevel[], n: number | null) => {
    const found = list.find((l) => l.level === n);
    return found ? <Localized {...inLanguage(found.name, locale)} /> : "—";
  };
  const matrix = settings.priorityMatrix;
  const priority = suggestion.impact && suggestion.urgency ? matrix.matrix[suggestion.impact - 1]?.[suggestion.urgency - 1] ?? null : null;

  async function decide(path: "accept" | "dismiss", body: Record<string, unknown> = {}) {
    setState({ busy: true, error: null });
    try {
      await postJson(`/v1/incidents/${incidentId}/ai/triage/${suggestion.id}/${path}`, body, csrfToken);
      setState({ busy: false, error: null });
      router.refresh();
    } catch (error) {
      const code = error instanceof ApiError ? error.code : null;
      setState({ busy: false, error: code && t.has(`errors.${code}`) ? t(`errors.${code}`) : tAi(aiErrorKey(code)) });
    }
  }

  function correct(event: FormEvent) {
    event.preventDefault();
    void decide("accept", {
      categoryId: categoryId || null,
      impact: impact ? Number(impact) : null,
      urgency: urgency ? Number(urgency) : null,
      groupId: mayAssign && groupId ? groupId : null,
    });
  }

  const pending = suggestion.status === "Pending";
  return (
    <div className="space-y-2 rounded-md border border-line p-2 text-sm" data-testid="ai-triage">
      <h3 className="font-semibold">{t("title")}</h3>
      <p className="text-xs text-ink-muted">{t(`status.${suggestion.status}`)} · {t("confidence", { percent: formatNumber(suggestion.confidence, display.locale, display.digits) })}</p>
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
        <dt className="font-semibold">{t("category")}</dt>
        <dd>{suggestion.category ? <Localized {...inLanguage(suggestion.category.name, locale)} /> : "—"}</dd>
        <dt className="font-semibold">{t("priority")}</dt>
        <dd>
          {priority ? level(matrix.priorities, priority) : "—"}
          <span className="text-ink-muted"> ({t("impact")}: {level(matrix.impacts, suggestion.impact)}, {t("urgency")}: {level(matrix.urgencies, suggestion.urgency)})</span>
        </dd>
        <dt className="font-semibold">{t("group")}</dt>
        <dd>{suggestion.group ? <Localized {...inLanguage(suggestion.group.name, locale)} /> : "—"}</dd>
      </dl>
      {suggestion.reason && <p className="text-ink-muted" lang="en" dir="ltr">{suggestion.reason}</p>}
      {pending && mayEdit && !correcting && (
        <div className="flex flex-wrap gap-2">
          <button type="button" className={primary} disabled={state.busy} onClick={() => void decide("accept", mayAssign ? {} : { groupId: null })}>{t("accept")}</button>
          <button type="button" className={secondaryButtonClass} disabled={state.busy} onClick={() => setCorrecting(true)}>{t("correct")}</button>
          <button type="button" className={secondaryButtonClass} disabled={state.busy} onClick={() => void decide("dismiss")}>{t("dismiss")}</button>
        </div>
      )}
      {pending && correcting && (
        <form onSubmit={correct} className="space-y-2" aria-label={t("correct")}>
          <label className="block space-y-1">
            <span className="font-semibold">{t("category")}</span>
            <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass} id={`${id}-category`}>
              <option value="">{t("keep")}</option>
              {categories.filter((c) => c.selectable).map((c) => {
                const path = inLanguage(c.path, locale);
                return <option key={c.id} value={c.id} lang={path.lang}>{path.value}</option>;
              })}
            </select>
          </label>
          <div className="flex flex-wrap gap-2">
            {(["impact", "urgency"] as const).map((field) => (
              <label key={field} className="space-y-1">
                <span className="block font-semibold">{t(field)}</span>
                <select value={field === "impact" ? impact : urgency} onChange={(e) => (field === "impact" ? setImpact : setUrgency)(e.target.value)} className={inputClass}>
                  {(field === "impact" ? matrix.impacts : matrix.urgencies).map((l) => (
                    <option key={l.level} value={l.level}>{inLanguage(l.name, locale).value}</option>
                  ))}
                </select>
              </label>
            ))}
          </div>
          {mayAssign && (
            <label className="block space-y-1">
              <span className="font-semibold">{t("group")}</span>
              <select value={groupId} onChange={(e) => setGroupId(e.target.value)} className={inputClass}>
                <option value="">{t("keep")}</option>
                {groups.filter((g) => g.active).map((g) => <option key={g.id} value={g.id}>{inLanguage(g.name, locale).value}</option>)}
              </select>
            </label>
          )}
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={primary} disabled={state.busy}>{t("saveCorrection")}</button>
            <button type="button" className={secondaryButtonClass} onClick={() => setCorrecting(false)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      {state.error && <p role="alert" className="text-bad">{state.error}</p>}
    </div>
  );
}

function Duplicates({ incidentId, duplicates, csrfToken, mayEdit }: { incidentId: string; duplicates: DuplicateCandidate[]; csrfToken: string; mayEdit: boolean }) {
  const t = useTranslations("ai.assist.duplicates");
  const tAi = useTranslations("ai.assist");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  async function decide(candidate: DuplicateCandidate, path: "link" | "dismiss") {
    setError(null);
    try {
      await postJson(`/v1/incidents/${incidentId}/ai/duplicates/${candidate.id}/${path}`, {}, csrfToken);
      router.refresh();
    } catch (refused) {
      const code = refused instanceof ApiError ? refused.code : null;
      setError(code && t.has(`errors.${code}`) ? t(`errors.${code}`) : tAi(aiErrorKey(code)));
    }
  }
  return (
    <div className="space-y-2 rounded-md border border-line p-2 text-sm" data-testid="ai-duplicates">
      <h3 className="font-semibold">{t("title")}</h3>
      <ul className="space-y-2">
        {duplicates.map((d) => (
          <li key={d.id} className="space-y-1">
            <Link href={`/workspace/${d.incident.id}`} className="font-semibold text-brand underline-offset-4 hover:underline">
              <span dir="ltr" className="font-mono">{d.incident.number}</span> <span dir="auto">{d.incident.title}</span>
            </Link>
            <p className="text-ink-muted">{t("confidence", { percent: String(d.confidence) })}{d.reason && <> · <span lang="en" dir="ltr">{d.reason}</span></>}</p>
            {mayEdit && (
              <div className="flex flex-wrap gap-2">
                <button type="button" className={primary} onClick={() => void decide(d, "link")}>{t("link")}</button>
                <button type="button" className={secondaryButtonClass} onClick={() => void decide(d, "dismiss")}>{t("dismiss")}</button>
              </div>
            )}
          </li>
        ))}
      </ul>
      {error && <p role="alert" className="text-bad">{error}</p>}
    </div>
  );
}

function Summary({ incidentId, initial, csrfToken, canMake, display }: { incidentId: string; initial: AiSummary | null; csrfToken: string; canMake: boolean; display: DateDisplayOptions }) {
  const t = useTranslations("ai.assist.summary");
  const tAi = useTranslations("ai.assist");
  const [summary, setSummary] = useState(initial);
  const [state, setState] = useState<{ busy: boolean; error: string | null }>({ busy: false, error: null });
  async function make(refresh: boolean) {
    setState({ busy: true, error: null });
    try {
      setSummary(await postJson<AiSummary>(`/v1/incidents/${incidentId}/ai/summary`, { refresh, language: display.locale === "ar" ? "ar" : "en" }, csrfToken));
      setState({ busy: false, error: null });
    } catch (error) {
      setState({ busy: false, error: tAi(aiErrorKey(error instanceof ApiError ? error.code : null)) });
    }
  }
  return (
    <div className="space-y-2 rounded-md border border-line p-2 text-sm" data-testid="ai-summary">
      <h3 className="font-semibold">{t("title")}</h3>
      {summary && (
        <>
          <p lang={summary.language} dir={summary.language === "ar" ? "rtl" : "ltr"} className="whitespace-pre-wrap">{summary.text}</p>
          <p className="text-xs text-ink-muted">{t("made", { time: formatDateTime(new Date(summary.createdAt), display) })}</p>
        </>
      )}
      {canMake && (
        <button type="button" className={secondaryButtonClass} disabled={state.busy} onClick={() => void make(summary !== null)}>
          {state.busy ? tAi("working") : summary ? t("refresh") : t("make")}
        </button>
      )}
      <p aria-live="polite" className="text-bad">{state.error ?? ""}</p>
    </div>
  );
}

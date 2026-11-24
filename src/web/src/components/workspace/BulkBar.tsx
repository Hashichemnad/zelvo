"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, postJson } from "@/lib/client-api";
import { selectableNodes, textOf, type GroupSummary, type TreeNode } from "@/lib/directory";
import { inLanguage, levelName, type IncidentSettings, type Locale, type Person } from "@/lib/incidents";
import type { BulkAction, BulkResult } from "@/lib/workspace";
import { inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";

export interface BulkBarProps {
  selected: string[];
  /** Numbers of the selected incidents, for the results. */
  numbers: Record<string, string>;
  csrfToken: string;
  permissions: string[];
  settings: IncidentSettings | null;
  assignees: Person[] | null;
  groups: GroupSummary[];
  categories: TreeNode[];
  locations: TreeNode[];
  onDone: (result: BulkResult) => void;
  onClear: () => void;
}

/**
 * Bulk actions (module 5; decision 2026-11-24): one action for up to 100 selected incidents. Each
 * incident is checked like a single change; those that fail stay as they were and are listed.
 */
export function BulkBar({ selected, numbers, csrfToken, permissions, settings, assignees, groups, categories, locations, onDone, onClear }: BulkBarProps) {
  const t = useTranslations("workspace.bulk");
  const tStates = useTranslations("incidents.states");
  const tCommon = useTranslations("common");
  const locale = useLocale() as Locale;
  const id = useId();
  const mayAssign = permissions.includes("incidents.assign");
  const actions: BulkAction[] = [...(mayAssign ? ["Assign", "Assignment", "Route"] as const : []), "Transition", "Priority", "Category", "Location", "Reply"];
  const [action, setAction] = useState<BulkAction>(actions[0]!);
  const [assigneeId, setAssigneeId] = useState("");
  const [groupId, setGroupId] = useState("");
  const [state, setState] = useState("InProgress");
  const [reason, setReason] = useState("");
  const [notes, setNotes] = useState("");
  const [impact, setImpact] = useState(2);
  const [urgency, setUrgency] = useState(2);
  const [nodeId, setNodeId] = useState("");
  const [body, setBody] = useState("");
  const [visibility, setVisibility] = useState<"Public" | "Internal">("Public");
  const [status, setStatus] = useState<{ kind: "idle" | "working" } | { kind: "done"; result: BulkResult } | { kind: "failed"; message: string }>({ kind: "idle" });
  const matrix = settings?.priorityMatrix;

  async function apply(event: React.FormEvent) {
    event.preventDefault();
    setStatus({ kind: "working" });
    const payload: Record<string, unknown> = { ids: selected, action };
    if (action === "Assign") payload.assigneeId = assigneeId || null;
    if (action === "Assignment") Object.assign(payload, { groupId: groupId || null, assigneeId: assigneeId || null });
    if (action === "Transition") Object.assign(payload, { state, onHoldReason: state === "OnHold" ? reason : null, resolutionNotes: state === "Resolved" ? notes : null });
    if (action === "Priority") Object.assign(payload, { impact, urgency });
    if (action === "Category") payload.categoryId = nodeId || null;
    if (action === "Location") payload.locationId = nodeId || null;
    if (action === "Reply") Object.assign(payload, { body, visibility, language: locale });
    try {
      const result = await postJson<BulkResult>("/v1/incidents/bulk", payload, csrfToken);
      setStatus({ kind: "done", result });
      onDone(result);
    } catch (error) {
      setStatus({ kind: "failed", message: error instanceof ApiError && error.rateLimited ? tCommon("rateLimited") : t("failed") });
    }
  }

  const reasonText = (code: string | null) => (code && t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other"));
  const label = "space-y-1 text-sm";
  return (
    <form onSubmit={apply} aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border-2 border-brand bg-surface p-3" data-testid="bulk-bar">
      <h2 id={`${id}-heading`} className="font-semibold">{t("heading", { count: selected.length })}</h2>
      <div className="flex flex-wrap items-end gap-3">
        <label className={label}>
          <span className="block font-semibold">{t("action")}</span>
          <select value={action} onChange={(e) => { setAction(e.target.value as BulkAction); setNodeId(""); }} className={inputClass}>
            {actions.map((a) => <option key={a} value={a}>{t(`actions.${a}`)}</option>)}
          </select>
        </label>
        {action === "Assignment" && (
          <label className={label}>
            <span className="block font-semibold">{t("group")}</span>
            <select value={groupId} onChange={(e) => setGroupId(e.target.value)} className={inputClass}>
              <option value="">{t("none")}</option>
              {groups.filter((g) => g.active).map((g) => <option key={g.id} value={g.id}>{inLanguage(g.name, locale).value}</option>)}
            </select>
          </label>
        )}
        {(action === "Assign" || action === "Assignment") && (
          <label className={label}>
            <span className="block font-semibold">{t("assignee")}</span>
            <select value={assigneeId} onChange={(e) => setAssigneeId(e.target.value)} className={inputClass}>
              <option value="">{t("nobody")}</option>
              {(assignees ?? []).map((p) => <option key={p.id} value={p.id}>{p.displayName ?? p.id}</option>)}
            </select>
          </label>
        )}
        {action === "Transition" && (
          <>
            <label className={label}>
              <span className="block font-semibold">{t("state")}</span>
              <select value={state} onChange={(e) => setState(e.target.value)} className={inputClass}>
                {(["InProgress", "OnHold", "Resolved", "Closed", "Reopened"] as const).map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
              </select>
            </label>
            {state === "OnHold" && (
              <label className={label}>
                <span className="block font-semibold">{t("reason")}</span>
                <select value={reason} onChange={(e) => setReason(e.target.value)} required className={inputClass}>
                  <option value="">{t("chooseReason")}</option>
                  {(settings?.onHoldReasons ?? []).filter((r) => r.active).map((r) => <option key={r.key} value={r.key}>{inLanguage(r.name, locale).value}</option>)}
                </select>
              </label>
            )}
            {state === "Resolved" && (
              <label className={`${label} min-w-64 flex-1`}>
                <span className="block font-semibold">{t("notes")}</span>
                <textarea value={notes} onChange={(e) => setNotes(e.target.value)} required rows={2} dir="auto" className={inputClass} />
              </label>
            )}
          </>
        )}
        {action === "Priority" && matrix && (
          <>
            {(["impact", "urgency"] as const).map((name) => (
              <label key={name} className={label}>
                <span className="block font-semibold">{t(name)}</span>
                <select value={name === "impact" ? impact : urgency} onChange={(e) => (name === "impact" ? setImpact : setUrgency)(Number(e.target.value))} className={inputClass}>
                  {(name === "impact" ? matrix.impacts : matrix.urgencies).map((l) => (
                    <option key={l.level} value={l.level}>{levelName(name === "impact" ? matrix.impacts : matrix.urgencies, l.level, locale)}</option>
                  ))}
                </select>
              </label>
            ))}
          </>
        )}
        {(action === "Category" || action === "Location") && (
          <label className={label}>
            <span className="block font-semibold">{t(action === "Category" ? "category" : "location")}</span>
            <select value={nodeId} onChange={(e) => setNodeId(e.target.value)} className={inputClass}>
              <option value="">{t("none")}</option>
              {selectableNodes(action === "Category" ? categories : locations).map((n) => (
                <option key={n.id} value={n.id}>{textOf(n.path, locale)}</option>
              ))}
            </select>
          </label>
        )}
        {action === "Reply" && (
          <>
            <fieldset className="flex gap-3 pb-2 text-sm">
              <legend className="sr-only">{t("visibility")}</legend>
              {(["Public", "Internal"] as const).map((v) => (
                <label key={v} className="flex items-center gap-1">
                  <input type="radio" name={`${id}-visibility`} checked={visibility === v} onChange={() => setVisibility(v)} />
                  {t(v === "Public" ? "public" : "internal")}
                </label>
              ))}
            </fieldset>
            <label className={`${label} min-w-64 flex-1`}>
              <span className="block font-semibold">{t("body")}</span>
              <textarea value={body} onChange={(e) => setBody(e.target.value)} required rows={2} dir="auto" lang={locale} className={inputClass} />
            </label>
          </>
        )}
        <button type="submit" disabled={status.kind === "working"} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface disabled:opacity-60">
          {t("apply")}
        </button>
        <button type="button" onClick={onClear} className={secondaryButtonClass}>{t("clear")}</button>
      </div>
      <div aria-live="polite">
        {status.kind === "working" && <p>{t("working")}</p>}
        {status.kind === "failed" && <p className="text-bad">{status.message}</p>}
        {status.kind === "done" && (
          <div className="space-y-1 text-sm">
            <p className="font-semibold">{t("result", { succeeded: status.result.succeeded, failed: status.result.failed })}</p>
            {status.result.failed > 0 && (
              <ul className="list-inside list-disc">
                {status.result.results.filter((r) => !r.ok).map((r) => (
                  <li key={r.id}><span dir="ltr" className="font-mono">{numbers[r.id] ?? r.id}</span>: {reasonText(r.code)}</li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>
    </form>
  );
}

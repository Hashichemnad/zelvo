"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { getJson, postJson, putJson } from "@/lib/client-api";
import type { NamedRef } from "@/lib/directory";
import { inLanguage, type Locale, type Person } from "@/lib/incidents";
import type { Problem, ProblemTarget } from "@/lib/problems";
import { ProblemStatusLine, useSaver } from "./common";

interface OnHoldReason {
  key: string;
  name: { en: string | null; ar: string | null };
  active: boolean;
}

/**
 * Moves a problem through its stages (problems.edit; the known error, resolving, closing and
 * reopening need problems.manage, and the API offers only what the caller may do) and sets its
 * group and assignee (problems.manage). Resolving can also resolve the open linked incidents.
 */
export function ProblemWorkPanel({ problem, reasons, groups, canAssign, csrfToken }: {
  problem: Problem;
  reasons: OnHoldReason[];
  groups: NamedRef[];
  canAssign: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("problems.work");
  const tTargets = useTranslations("problems.targets");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [target, setTarget] = useState<ProblemTarget | null>(null);
  const [reason, setReason] = useState("");
  const [notes, setNotes] = useState("");
  const [resolveIncidents, setResolveIncidents] = useState(problem.openIncidentCount > 0);
  const [groupId, setGroupId] = useState(problem.group?.id ?? "");
  const [assigneeId, setAssigneeId] = useState(problem.assignee?.id ?? "");
  const [people, setPeople] = useState<Person[]>([]);
  const headers = { "If-Match": problem.etag };

  useEffect(() => {
    if (!canAssign) return;
    let live = true;
    getJson<Person[]>(`/v1/problems/assignees${groupId ? `?groupId=${groupId}` : ""}`)
      .then((list) => live && setPeople(list))
      .catch(() => live && setPeople([]));
    return () => {
      live = false;
    };
  }, [canAssign, groupId]);

  function move(to: ProblemTarget) {
    if (to === "OnHold" || to === "Resolved") {
      setTarget(to);
      return;
    }
    void run(async () => {
      await postJson(`/v1/problems/${problem.id}/transitions`, { to }, csrfToken, headers);
      router.refresh();
    });
  }

  function confirm() {
    void run(async () => {
      await postJson(`/v1/problems/${problem.id}/transitions`,
        target === "OnHold" ? { to: target, onHoldReason: reason } : { to: target, resolutionNotes: notes, resolveIncidents }, csrfToken, headers);
      setTarget(null);
      router.refresh();
    });
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="problem-work">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {problem.nextTargets.length === 0 ? (
        <p className="text-ink-muted">{t("noMoves")}</p>
      ) : (
        <div role="group" aria-label={t("move")} className="flex flex-wrap gap-2">
          {problem.nextTargets.map((to) => (
            <button key={to} type="button" className={secondaryButtonClass} disabled={busy} aria-pressed={target === to} onClick={() => move(to)}>
              {tTargets(to)}
            </button>
          ))}
        </div>
      )}
      {target === "OnHold" && (
        <div className="space-y-2">
          <label htmlFor={`${id}-reason`} className="block font-semibold">{t("reason")}</label>
          <select id={`${id}-reason`} value={reason} onChange={(e) => setReason(e.target.value)} className={inputClass}>
            <option value="" />
            {reasons.filter((r) => r.active).map((r) => <option key={r.key} value={r.key}>{inLanguage(r.name, locale).value}</option>)}
          </select>
          <button type="button" className={buttonClass} disabled={busy || !reason} onClick={confirm}>{t("confirm")}</button>
        </div>
      )}
      {target === "Resolved" && (
        <div className="space-y-2">
          <label htmlFor={`${id}-notes`} className="block font-semibold">{t("notes")}</label>
          <textarea id={`${id}-notes`} value={notes} onChange={(e) => setNotes(e.target.value)} rows={3} maxLength={10000} dir="auto" className={inputClass} />
          {problem.openIncidentCount > 0 && (
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={resolveIncidents} onChange={(e) => setResolveIncidents(e.target.checked)} className="size-5 accent-brand" />
              {t("resolveIncidents", { count: problem.openIncidentCount })}
            </label>
          )}
          <button type="button" className={buttonClass} disabled={busy || !notes.trim()} onClick={confirm}>{t("confirm")}</button>
        </div>
      )}
      {canAssign && problem.state !== "Closed" && (
        <fieldset className="space-y-2">
          <legend className="font-semibold">{t("assignment")}</legend>
          <div className="grid gap-2 sm:grid-cols-2">
            <label className="space-y-1 text-sm">
              <span className="block">{t("group")}</span>
              <select value={groupId} onChange={(e) => (setGroupId(e.target.value), setAssigneeId(""))} className={inputClass}>
                <option value="">{t("noGroup")}</option>
                {groups.map((g) => <option key={g.id} value={g.id}>{inLanguage(g.name, locale).value}</option>)}
              </select>
            </label>
            <label className="space-y-1 text-sm">
              <span className="block">{t("assignee")}</span>
              <select value={assigneeId} onChange={(e) => setAssigneeId(e.target.value)} className={inputClass}>
                <option value="">{t("nobody")}</option>
                {people.map((p) => <option key={p.id} value={p.id}>{p.displayName ?? p.id}</option>)}
              </select>
            </label>
          </div>
          <div className="flex flex-wrap gap-2">
            <button type="button" className={secondaryButtonClass} disabled={busy}
              onClick={() => void run(async () => {
                await putJson(`/v1/problems/${problem.id}/assignment`, { groupId: groupId || null, assigneeId: assigneeId || null }, csrfToken, headers);
                router.refresh();
              })}>
              {t("assign")}
            </button>
            {problem.state !== "Resolved" && (
              <button type="button" className={secondaryButtonClass} disabled={busy}
                onClick={() => void run(async () => {
                  await postJson(`/v1/problems/${problem.id}/route`, {}, csrfToken);
                  router.refresh();
                })}>
                {t("route")}
              </button>
            )}
          </div>
        </fieldset>
      )}
      <ProblemStatusLine status={status} />
    </section>
  );
}

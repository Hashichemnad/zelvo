"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { getJson, postJson, putJson } from "@/lib/client-api";
import { changeOutcomes, type Change, type ChangeAction, type ChangeOutcome } from "@/lib/changes";
import type { NamedRef } from "@/lib/directory";
import { inLanguage, type Locale, type Person } from "@/lib/incidents";
import { ChangeStatusLine, useSaver } from "./common";

const transitionOf: Partial<Record<ChangeAction, string>> = {
  Replan: "Replan",
  StartImplementation: "StartImplementation",
  Implemented: "Implemented",
  Cancel: "Cancel",
};

/**
 * Moves a change on (task H3): submit to its approval path, replan, start implementing, mark
 * implemented (with notes), cancel (with a reason) and, for the change manager, the
 * post-implementation review that closes it. The API offers only what the caller may do. Sets its
 * group and assignee (changes.manage).
 */
export function ChangeWorkPanel({ change, groups, canAssign, csrfToken }: {
  change: Change;
  groups: NamedRef[];
  canAssign: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("changes.work");
  const tActions = useTranslations("changes.actions");
  const tOutcomes = useTranslations("changes.outcomes");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [pending, setPending] = useState<ChangeAction | null>(null);
  const [notes, setNotes] = useState("");
  const [outcome, setOutcome] = useState<ChangeOutcome>("Successful");
  const [groupId, setGroupId] = useState(change.group?.id ?? "");
  const [assigneeId, setAssigneeId] = useState(change.assignee?.id ?? "");
  const [people, setPeople] = useState<Person[]>([]);
  const headers = { "If-Match": change.etag };

  useEffect(() => {
    if (!canAssign) return;
    let live = true;
    getJson<Person[]>(`/v1/changes/assignees${groupId ? `?groupId=${groupId}` : ""}`)
      .then((list) => live && setPeople(list))
      .catch(() => live && setPeople([]));
    return () => {
      live = false;
    };
  }, [canAssign, groupId]);

  function act(action: ChangeAction) {
    if (action === "Implemented" || action === "Cancel" || action === "Review") {
      setPending(action);
      return;
    }
    void run(async () => {
      if (action === "Submit") await postJson(`/v1/changes/${change.id}/submit`, {}, csrfToken, headers);
      else await postJson(`/v1/changes/${change.id}/transitions`, { to: transitionOf[action] }, csrfToken, headers);
      router.refresh();
    });
  }

  function confirm() {
    void run(async () => {
      if (pending === "Review") await postJson(`/v1/changes/${change.id}/review`, { outcome, notes: notes || undefined }, csrfToken, headers);
      else await postJson(`/v1/changes/${change.id}/transitions`, { to: transitionOf[pending!], notes }, csrfToken, headers);
      setPending(null);
      router.refresh();
    });
  }

  const conflicts = change.conflicts ?? [];
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="change-work">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {change.nextActions.length === 0 ? (
        <p className="text-ink-muted">{t("noActions")}</p>
      ) : (
        <div role="group" aria-label={t("actions")} className="flex flex-wrap gap-2">
          {change.nextActions.map((a) => (
            <button key={a} type="button" className={a === "Submit" ? buttonClass : secondaryButtonClass} disabled={busy} aria-pressed={pending === a} onClick={() => act(a)}>
              {tActions(a)}
            </button>
          ))}
        </div>
      )}
      {change.nextActions.includes("Submit") && conflicts.length > 0 && (
        <p role="note" className="text-warn">{t("submitConflicts", { count: conflicts.length })}</p>
      )}
      {(pending === "Implemented" || pending === "Cancel") && (
        <div className="space-y-2">
          <label htmlFor={`${id}-notes`} className="block font-semibold">{pending === "Cancel" ? t("cancelReason") : t("implementedNotes")}</label>
          <textarea id={`${id}-notes`} value={notes} onChange={(e) => setNotes(e.target.value)} rows={3} maxLength={10000} dir="auto" className={inputClass} />
          <button type="button" className={buttonClass} disabled={busy || !notes.trim()} onClick={confirm}>{t("confirm")}</button>
        </div>
      )}
      {pending === "Review" && (
        <div className="space-y-3" data-testid="change-review-form">
          <fieldset className="space-y-1">
            <legend className="font-semibold">{t("outcome")}</legend>
            {changeOutcomes.map((o) => (
              <label key={o} className="flex items-center gap-2">
                <input type="radio" name={`${id}-outcome`} className="size-5 accent-brand" checked={outcome === o} onChange={() => setOutcome(o)} />
                {tOutcomes(o)}
              </label>
            ))}
          </fieldset>
          <label htmlFor={`${id}-review`} className="block font-semibold">{t("reviewNotes")}</label>
          <textarea id={`${id}-review`} value={notes} onChange={(e) => setNotes(e.target.value)} rows={4} maxLength={20000} dir="auto" className={inputClass} />
          <button type="button" className={buttonClass} disabled={busy || (outcome !== "Successful" && !notes.trim())} onClick={confirm}>{t("closeWithReview")}</button>
        </div>
      )}
      {canAssign && change.state !== "Closed" && (
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
          <button type="button" className={secondaryButtonClass} disabled={busy}
            onClick={() => void run(async () => {
              await putJson(`/v1/changes/${change.id}/assignment`, { groupId: groupId || null, assigneeId: assigneeId || null }, csrfToken, headers);
              router.refresh();
            })}>
            {t("assign")}
          </button>
        </fieldset>
      )}
      <ChangeStatusLine status={status} />
    </section>
  );
}

"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { SaveStatusLine, useSaver } from "@/components/directory/common";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { getJson, postJson, putJson } from "@/lib/client-api";
import type { NamedRef } from "@/lib/directory";
import { inLanguage, type Locale, type Person } from "@/lib/incidents";
import type { CustomRecord, RecordStateRef } from "@/lib/records";

interface OnHoldReason {
  key: string;
  name: { en: string | null; ar: string | null };
  active: boolean;
}

/**
 * Moves a record to another state of its type (the API offers the states the caller may choose:
 * staff with the type's edit permission, or the requester reopening a resolved record) and, with
 * the type's assign permission, sets its group and assignee.
 */
export function RecordWorkPanel({ record, reasons, groups, canAssign, csrfToken }: {
  record: CustomRecord;
  reasons: OnHoldReason[];
  groups: NamedRef[];
  canAssign: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("records.work");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [target, setTarget] = useState<RecordStateRef | null>(null);
  const [reason, setReason] = useState("");
  const [notes, setNotes] = useState("");
  const [groupId, setGroupId] = useState(record.group?.id ?? "");
  const [assigneeId, setAssigneeId] = useState(record.assignee?.id ?? "");
  const [people, setPeople] = useState<Person[]>([]);
  const headers = { "If-Match": record.etag };
  const base = `/v1/records/${record.type}/${record.id}`;

  useEffect(() => {
    if (!canAssign) return;
    let live = true;
    getJson<Person[]>(`/v1/records/${record.type}/assignees${groupId ? `?groupId=${groupId}` : ""}`)
      .then((list) => live && setPeople(list))
      .catch(() => live && setPeople([]));
    return () => {
      live = false;
    };
  }, [canAssign, groupId, record.type]);

  const needsMore = (s: RecordStateRef) =>
    (s.category === "OnHold" && record.stateOfType.category !== "OnHold") || (s.category === "Resolved" && record.stateOfType.category !== "Resolved");

  function move(s: RecordStateRef, extra: Record<string, string> = {}) {
    void run(async () => {
      await postJson(`${base}/transitions`, { stateKey: s.key, ...extra }, csrfToken, headers);
      setTarget(null);
      router.refresh();
    });
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="record-work">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {record.awaitingApproval && <p className="font-semibold">{t("awaitingApproval")}</p>}
      {record.nextStates.length === 0 ? <p className="text-ink-muted">{t("noMoves")}</p> : (
        <div role="group" aria-label={t("move")} className="flex flex-wrap gap-2">
          {record.nextStates.map((s) => (
            <button key={s.key} type="button" className={secondaryButtonClass} disabled={busy} aria-pressed={target?.key === s.key}
              onClick={() => (needsMore(s) ? setTarget(s) : move(s))}>
              {t("moveTo", { state: inLanguage(s.name, locale).value })}
            </button>
          ))}
        </div>
      )}
      {target?.category === "OnHold" && (
        <div className="space-y-2">
          <label htmlFor={`${id}-reason`} className="block font-semibold">{t("reason")}</label>
          <select id={`${id}-reason`} value={reason} onChange={(e) => setReason(e.target.value)} className={inputClass}>
            <option value="" />
            {reasons.filter((r) => r.active).map((r) => <option key={r.key} value={r.key}>{inLanguage(r.name, locale).value}</option>)}
          </select>
          <button type="button" className={buttonClass} disabled={busy || !reason} onClick={() => move(target, { onHoldReason: reason })}>{t("confirm")}</button>
        </div>
      )}
      {target?.category === "Resolved" && (
        <div className="space-y-2">
          <label htmlFor={`${id}-notes`} className="block font-semibold">{t("notes")}</label>
          <textarea id={`${id}-notes`} value={notes} onChange={(e) => setNotes(e.target.value)} rows={3} maxLength={10000} dir="auto" className={inputClass} />
          <button type="button" className={buttonClass} disabled={busy || !notes.trim()} onClick={() => move(target, { resolutionNotes: notes })}>{t("confirm")}</button>
        </div>
      )}
      {canAssign && record.state !== "Closed" && (
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
                await putJson(`${base}/assignment`, { groupId: groupId || null, assigneeId: assigneeId || null }, csrfToken, headers);
                router.refresh();
              })}>
              {t("assign")}
            </button>
            {!record.awaitingApproval && record.state !== "Resolved" && (
              <button type="button" className={secondaryButtonClass} disabled={busy}
                onClick={() => void run(async () => {
                  await postJson(`${base}/route`, {}, csrfToken);
                  router.refresh();
                })}>
                {t("route")}
              </button>
            )}
          </div>
        </fieldset>
      )}
      <SaveStatusLine status={status} />
    </section>
  );
}

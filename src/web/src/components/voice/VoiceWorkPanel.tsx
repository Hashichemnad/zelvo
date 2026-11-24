"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { useSaver } from "@/components/directory/common";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { getJson, patchJson, postJson, putJson } from "@/lib/client-api";
import { textOf, type GroupSummary } from "@/lib/directory";
import type { IncidentSettings, Locale, Person, TicketState } from "@/lib/incidents";
import { suggestionReviews, type SuggestionReview, type VoiceRecord, type VoiceSettings } from "@/lib/voice";
import { VoiceStatusLine } from "./VoiceViews";

/**
 * Staff work on a complaint, inquiry or suggestion: assignment and "Route again", a complaint's
 * severity and root cause, state changes (with the resolution summary or answer), a suggestion's
 * review with feedback, and converting an inquiry to a complaint or a service request.
 */
export function VoiceWorkPanel({ record, settings, incidentSettings, groups, assignees, services, permissions, csrfToken }: {
  record: VoiceRecord; settings: VoiceSettings | null; incidentSettings: IncidentSettings | null; groups: GroupSummary[]; assignees: Person[] | null;
  services: { id: string; name: { en: string | null; ar: string | null } }[]; permissions: string[]; csrfToken: string;
}) {
  const t = useTranslations("voice.work");
  const tVoice = useTranslations("voice");
  const tStates = useTranslations("incidents.states");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [groupId, setGroupId] = useState(record.group?.id ?? "");
  const [assigneeId, setAssigneeId] = useState(record.assignee?.id ?? "");
  const [people, setPeople] = useState<Person[] | null>(assignees);
  const [severity, setSeverity] = useState(String(record.severity ?? 2));
  const [rootCause, setRootCause] = useState(record.rootCauseKey ?? "");
  const [target, setTarget] = useState<TicketState | "">("");
  const [reason, setReason] = useState("");
  const [notes, setNotes] = useState("");
  const [review, setReview] = useState<SuggestionReview>(record.review ?? "UnderReview");
  const [feedback, setFeedback] = useState("");
  const [convertTo, setConvertTo] = useState<"Complaint" | "ServiceRequest">("Complaint");
  const [serviceId, setServiceId] = useState("");
  const base = `/v1/customer-voice/${record.id}`;
  const ifMatch = { "If-Match": record.etag };
  const open = record.state !== "Resolved" && record.state !== "Closed";
  const edit = permissions.includes("voice.edit");
  const done = async (action: () => Promise<unknown>) => {
    if ((await run(action)) !== undefined) router.refresh();
  };
  const reasons = incidentSettings?.onHoldReasons.filter((r) => r.active) ?? [];
  const box = "space-y-2 rounded-lg border border-line p-4";

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4" data-testid="voice-work">
      <h2 id={`${id}-heading`} className="text-xl font-bold">{t("heading")}</h2>
      {permissions.includes("voice.assign") && record.state !== "Closed" && (
        <form className={box} onSubmit={(e) => { e.preventDefault(); void done(() => putJson(`${base}/assignment`, { groupId: groupId || null, assigneeId: assigneeId || null }, csrfToken, ifMatch)); }}>
          <fieldset className="grid gap-2 sm:grid-cols-2">
            <legend className="font-semibold">{t("assignment")}</legend>
            {groups.length > 0 && (
              <div className="space-y-1">
                <label htmlFor={`${id}-group`} className="block text-sm">{t("group")}</label>
                <select id={`${id}-group`} value={groupId} className={fieldInput} onChange={(e) => {
                  setGroupId(e.target.value);
                  setAssigneeId("");
                  void getJson<Person[]>(`/v1/customer-voice/assignees${e.target.value ? `?${new URLSearchParams({ groupId: e.target.value })}` : ""}`).then(setPeople).catch(() => undefined);
                }}>
                  <option value="">{t("noGroup")}</option>
                  {groups.filter((g) => g.active || g.id === record.group?.id).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
                </select>
              </div>
            )}
            <div className="space-y-1">
              <label htmlFor={`${id}-assignee`} className="block text-sm">{t("assignee")}</label>
              <select id={`${id}-assignee`} value={assigneeId} onChange={(e) => setAssigneeId(e.target.value)} className={fieldInput}>
                <option value="">{tVoice("nobody")}</option>
                {(people ?? []).map((p) => <option key={p.id} value={p.id}>{p.displayName ?? p.id}</option>)}
              </select>
            </div>
          </fieldset>
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={primaryAction} disabled={busy}>{t("saveAssignment")}</button>
            {open && <button type="button" className={fieldButton} disabled={busy} onClick={() => void done(() => postJson(`${base}/route`, undefined, csrfToken))}>{t("routeAgain")}</button>}
          </div>
        </form>
      )}

      {edit && record.type === "Complaint" && record.state !== "Closed" && (
        <form className={box} onSubmit={(e) => { e.preventDefault(); void done(() => patchJson(base, { severity: Number(severity), rootCauseKey: rootCause || undefined }, csrfToken, ifMatch)); }}>
          <fieldset className="grid gap-2 sm:grid-cols-2">
            <legend className="font-semibold">{t("complaint")}</legend>
            <div className="space-y-1">
              <label htmlFor={`${id}-severity`} className="block text-sm">{tVoice("fields.severity")}</label>
              <select id={`${id}-severity`} value={severity} onChange={(e) => setSeverity(e.target.value)} className={fieldInput}>
                {(settings?.severities ?? []).map((s) => <option key={s.level} value={s.level}>{textOf(s.name, locale)}</option>)}
              </select>
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-cause`} className="block text-sm">{tVoice("fields.rootCause")}</label>
              <select id={`${id}-cause`} value={rootCause} onChange={(e) => setRootCause(e.target.value)} className={fieldInput}>
                <option value="">{t("chooseCause")}</option>
                {(settings?.rootCauses ?? []).filter((r) => r.active || r.key === record.rootCauseKey).map((r) => <option key={r.key} value={r.key}>{textOf(r.name, locale)}</option>)}
              </select>
            </div>
          </fieldset>
          <button type="submit" className={primaryAction} disabled={busy}>{t("saveComplaint")}</button>
        </form>
      )}

      {edit && record.nextStates.length > 0 && (
        <form className={box} onSubmit={(e) => {
          e.preventDefault();
          if (!target) return;
          void done(() => postJson(`${base}/transitions`, {
            state: target, onHoldReason: target === "OnHold" ? reason : undefined, resolutionNotes: target === "Resolved" ? notes : undefined,
          }, csrfToken, ifMatch));
        }}>
          <div className="space-y-1">
            <label htmlFor={`${id}-state`} className="block font-semibold">{t("changeState")}</label>
            <select id={`${id}-state`} value={target} onChange={(e) => setTarget(e.target.value as TicketState | "")} className={fieldInput}>
              <option value="">{t("chooseState")}</option>
              {record.nextStates.map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
            </select>
          </div>
          {target === "OnHold" && (
            <div className="space-y-1">
              <label htmlFor={`${id}-reason`} className="block text-sm">{t("onHoldReason")}</label>
              <select id={`${id}-reason`} value={reason} onChange={(e) => setReason(e.target.value)} required className={fieldInput}>
                <option value="">{t("chooseReason")}</option>
                {reasons.map((r) => <option key={r.key} value={r.key}>{textOf(r.name, locale)}</option>)}
              </select>
            </div>
          )}
          {target === "Resolved" && (
            <div className="space-y-1">
              <label htmlFor={`${id}-notes`} className="block text-sm">{t(record.type === "Inquiry" ? "answer" : "resolutionSummary")}</label>
              <textarea id={`${id}-notes`} value={notes} onChange={(e) => setNotes(e.target.value)} required rows={4} dir="auto" className={fieldInput} />
              {record.type === "Complaint" && <p className="text-sm text-ink-muted">{t("confirmationHint")}</p>}
            </div>
          )}
          <button type="submit" className={primaryAction} disabled={busy || !target}>{t("apply")}</button>
        </form>
      )}

      {edit && record.type === "Suggestion" && open && (
        <form className={box} onSubmit={(e) => { e.preventDefault(); void done(() => postJson(`${base}/review`, { review, feedback: feedback || undefined }, csrfToken, ifMatch)); }}>
          <div className="space-y-1">
            <label htmlFor={`${id}-review`} className="block font-semibold">{t("review")}</label>
            <select id={`${id}-review`} value={review} onChange={(e) => setReview(e.target.value as SuggestionReview)} className={fieldInput}>
              {suggestionReviews.map((r) => <option key={r} value={r}>{tVoice(`reviews.${r}`)}</option>)}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-feedback`} className="block text-sm">{t("feedback")}</label>
            <textarea id={`${id}-feedback`} value={feedback} onChange={(e) => setFeedback(e.target.value)} rows={3} dir="auto"
              required={review === "Implemented" || review === "Declined"} className={fieldInput} />
            <p className="text-sm text-ink-muted">{t("feedbackHint")}</p>
          </div>
          <button type="submit" className={primaryAction} disabled={busy}>{t("saveReview")}</button>
        </form>
      )}

      {edit && record.type === "Inquiry" && open && (
        <form className={box} onSubmit={(e) => {
          e.preventDefault();
          void done(() => postJson(`${base}/conversion`, { to: convertTo, serviceId: convertTo === "ServiceRequest" ? serviceId : undefined }, csrfToken, ifMatch));
        }}>
          <fieldset className="space-y-2">
            <legend className="font-semibold">{t("convert")}</legend>
            <p className="text-sm text-ink-muted">{t("convertHint")}</p>
            <div className="flex flex-wrap gap-4">
              {(["Complaint", "ServiceRequest"] as const).map((to) => (
                <label key={to} className="flex items-center gap-2">
                  <input type="radio" name={`${id}-to`} value={to} checked={convertTo === to} onChange={() => setConvertTo(to)} className="h-5 w-5" />
                  {t(`convertTo.${to}`)}
                </label>
              ))}
            </div>
            {convertTo === "ServiceRequest" && (
              <div className="space-y-1">
                <label htmlFor={`${id}-service`} className="block text-sm">{tVoice("fields.service")}</label>
                <select id={`${id}-service`} value={serviceId} onChange={(e) => setServiceId(e.target.value)} required className={fieldInput}>
                  <option value="">{t("chooseService")}</option>
                  {services.map((s) => <option key={s.id} value={s.id}>{textOf(s.name, locale)}</option>)}
                </select>
              </div>
            )}
          </fieldset>
          <button type="submit" className={primaryAction} disabled={busy || (convertTo === "ServiceRequest" && !serviceId)}>{t("convertButton")}</button>
        </form>
      )}
      <VoiceStatusLine status={status} />
    </section>
  );
}

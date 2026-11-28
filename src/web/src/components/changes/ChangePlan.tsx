"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { buttonClass, inputClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { patchJson, putJson } from "@/lib/client-api";
import { assess, fromLocalInput, toLocalInput, type Change, type ChangeSettings } from "@/lib/changes";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale } from "@/lib/incidents";
import { ChangeStatusLine, RiskBadge, useSaver } from "./common";

function Plan({ label, text }: { label: string; text: string | null }) {
  const t = useTranslations("changes.plan");
  return (
    <div className="space-y-1">
      <h3 className="font-semibold">{label}</h3>
      {text ? <p className="whitespace-pre-wrap" dir="auto">{text}</p> : <p className="text-ink-muted">{t("none")}</p>}
    </div>
  );
}

/**
 * A change's description, window and implementation, test and back-out plans (task H3). They are
 * edited while the change is planned (stage New; changes.edit); afterwards they are read-only
 * until the change is replanned.
 */
export function ChangePlan({ change, canEdit, csrfToken, display }: {
  change: Change;
  canEdit: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("changes.plan");
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [editing, setEditing] = useState(false);
  const [description, setDescription] = useState(change.description ?? "");
  const [start, setStart] = useState(toLocalInput(change.plannedStart));
  const [end, setEnd] = useState(toLocalInput(change.plannedEnd));
  const [implementation, setImplementation] = useState(change.implementationPlan ?? "");
  const [test, setTest] = useState(change.testPlan ?? "");
  const [backout, setBackout] = useState(change.backoutPlan ?? "");
  const editable = canEdit && change.stage === "New";
  const windowText = change.plannedStart && change.plannedEnd
    ? `${formatDateTime(new Date(change.plannedStart), display)} – ${formatDateTime(new Date(change.plannedEnd), display)}`
    : t("noWindow");

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="change-plan">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
        {editable && !editing && <button type="button" className={buttonClass} onClick={() => setEditing(true)}>{t("edit")}</button>}
      </div>
      {!editing ? (
        <div className="space-y-3">
          {change.description && <p className="whitespace-pre-wrap" dir="auto">{change.description}</p>}
          <p><span className="font-semibold">{t("window")}:</span> <span>{windowText}</span></p>
          {change.actualStart && (
            <p><span className="font-semibold">{t("actual")}:</span>{" "}
              <span>{formatDateTime(new Date(change.actualStart), display)}{change.actualEnd ? ` – ${formatDateTime(new Date(change.actualEnd), display)}` : ""}</span></p>
          )}
          <Plan label={t("implementationPlan")} text={change.implementationPlan} />
          <Plan label={t("testPlan")} text={change.testPlan} />
          <Plan label={t("backoutPlan")} text={change.backoutPlan} />
        </div>
      ) : (
        <form className="space-y-3" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            const s = fromLocalInput(start);
            const en = fromLocalInput(end);
            await patchJson(`/v1/changes/${change.id}`, {
              description, implementationPlan: implementation, testPlan: test, backoutPlan: backout,
              ...(s && en ? { plannedStart: s, plannedEnd: en } : { clearWindow: !s && !en }),
            }, csrfToken, { "If-Match": change.etag });
            setEditing(false);
            router.refresh();
          });
        }}>
          <label className="block space-y-1">
            <span className="block font-semibold">{t("description")}</span>
            <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={3} maxLength={20000} dir="auto" className={inputClass} />
          </label>
          <fieldset className="grid gap-3 sm:grid-cols-2">
            <legend className="mb-1 font-semibold">{t("window")}</legend>
            <label className="block space-y-1 text-sm">
              <span className="block">{t("start")}</span>
              <input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} className={inputClass} dir="ltr" />
            </label>
            <label className="block space-y-1 text-sm">
              <span className="block">{t("end")}</span>
              <input type="datetime-local" value={end} onChange={(e) => setEnd(e.target.value)} className={inputClass} dir="ltr" />
            </label>
          </fieldset>
          {([["implementationPlan", implementation, setImplementation], ["testPlan", test, setTest], ["backoutPlan", backout, setBackout]] as const).map(([key, value, set]) => (
            <label key={key} className="block space-y-1">
              <span className="block font-semibold">{t(key)}</span>
              <textarea value={value} onChange={(e) => set(e.target.value)} rows={4} maxLength={20000} dir="auto" className={inputClass} />
            </label>
          ))}
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={buttonClass} disabled={busy}>{t("save")}</button>
            <button type="button" className="px-2 py-2 font-semibold text-brand underline" onClick={() => setEditing(false)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      <ChangeStatusLine status={status} />
    </section>
  );
}

/**
 * The risk and impact questionnaire (task H3): one answer per question, with the score and level
 * worked out as the person answers (the server works them out again). Standard changes are
 * pre-approved and low risk; they have no questionnaire.
 */
export function RiskQuestionnaire({ change, settings, canEdit, csrfToken }: {
  change: Change;
  settings: ChangeSettings;
  canEdit: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("changes.risk");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [answers, setAnswers] = useState<Record<string, string>>(change.riskAnswers);
  const editable = canEdit && change.stage === "New" && change.kind !== "Standard";
  const live = assess(settings, answers);
  const questions = settings.questions.filter((q) => q.active || change.riskAnswers[q.key]);

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="change-risk">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      <p>
        <span className="font-semibold">{t("level")}:</span> <RiskBadge level={change.riskLevel} />{" "}
        {change.riskScore !== null && <span className="text-ink-muted">{t("score", { score: change.riskScore })}</span>}
      </p>
      {change.kind === "Standard" ? (
        <p className="text-ink-muted">{t("standard")}</p>
      ) : (
        <form className="space-y-4" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            await putJson(`/v1/changes/${change.id}/risk`, { answers }, csrfToken, { "If-Match": change.etag });
            router.refresh();
          });
        }}>
          {questions.map((q) => (
            <fieldset key={q.key} className="space-y-1" disabled={!editable}>
              <legend className="font-semibold" dir="auto">{inLanguage(q.text, locale).value}</legend>
              {q.options.map((o) => (
                <label key={o.key} className="flex items-center gap-2">
                  <input type="radio" name={`${id}-${q.key}`} className="size-5 accent-brand" checked={answers[q.key] === o.key}
                    onChange={() => setAnswers((current) => ({ ...current, [q.key]: o.key }))} />
                  <span dir="auto">{inLanguage(o.text, locale).value}</span>
                </label>
              ))}
            </fieldset>
          ))}
          {editable && (
            <>
              <p role="status" aria-live="polite">{t("live", { score: live.score, level: t(`levels.${live.level}`), unanswered: live.unanswered })}</p>
              <button type="submit" className={buttonClass} disabled={busy}>{t("save")}</button>
            </>
          )}
        </form>
      )}
      <ChangeStatusLine status={status} />
    </section>
  );
}

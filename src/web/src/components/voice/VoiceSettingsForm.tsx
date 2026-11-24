"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { useSaver } from "@/components/directory/common";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { putJson } from "@/lib/client-api";
import type { VoiceSettings } from "@/lib/voice";
import { VoiceStatusLine } from "./VoiceViews";

type Text = { en: string; ar: string };
const text = (t: { en: string | null; ar: string | null }): Text => ({ en: t.en ?? "", ar: t.ar ?? "" });
const priorities = [1, 2, 3, 4, 5];

/** Two inputs side by side for a bilingual name (English left-to-right, Arabic right-to-left). */
function BilingualInput({ label, value, onChange, idPrefix }: { label: string; value: Text; onChange: (v: Text) => void; idPrefix: string }) {
  const t = useTranslations("voice.settings");
  return (
    <fieldset className="grid gap-2 sm:grid-cols-2">
      <legend className="text-sm font-semibold">{label}</legend>
      <div className="space-y-1">
        <label htmlFor={`${idPrefix}-en`} className="block text-sm">{t("english")}</label>
        <input id={`${idPrefix}-en`} lang="en" dir="ltr" value={value.en} maxLength={100} onChange={(e) => onChange({ ...value, en: e.target.value })} className={fieldInput} />
      </div>
      <div className="space-y-1">
        <label htmlFor={`${idPrefix}-ar`} className="block text-sm">{t("arabic")}</label>
        <input id={`${idPrefix}-ar`} lang="ar" dir="rtl" value={value.ar} maxLength={100} onChange={(e) => onChange({ ...value, ar: e.target.value })} className={fieldInput} />
      </div>
    </fieldset>
  );
}

/**
 * Settings → Customer voice (task C6): anonymous submission, complaint severities with their SLA
 * priorities, root causes, priorities of inquiries and suggestions, and the happiness meter's
 * scale and wording (GOV-2).
 */
export function VoiceSettingsForm({ settings, csrfToken }: { settings: VoiceSettings; csrfToken: string }) {
  const t = useTranslations("voice.settings");
  const id = useId();
  const router = useRouter();
  const { status, run, busy } = useSaver();
  const [anonymous, setAnonymous] = useState(settings.anonymousEnabled);
  const [severities, setSeverities] = useState(settings.severities.map((s) => ({ name: text(s.name), priority: s.priority })));
  const [causes, setCauses] = useState(settings.rootCauses.map((r) => ({ key: r.key, name: text(r.name), active: r.active, existing: true })));
  const [inquiryPriority, setInquiryPriority] = useState(settings.inquiryPriority);
  const [suggestionPriority, setSuggestionPriority] = useState(settings.suggestionPriority);
  const h = settings.happiness;
  const [enabled, setEnabled] = useState(h.enabled);
  const [points, setPoints] = useState<3 | 5>(h.points);
  const [question, setQuestion] = useState(text(h.question));
  const [portalQuestion, setPortalQuestion] = useState(text(h.portalQuestion));
  const [labels, setLabels] = useState(h.labels.map(text));
  const [askOnClosure, setAskOnClosure] = useState(h.askOnClosure);
  const [inPortal, setInPortal] = useState(h.inPortal);

  const prioritySelect = (label: string, value: number, set: (n: number) => void, key: string) => (
    <div className="space-y-1">
      <label htmlFor={`${id}-${key}`} className="block text-sm">{label}</label>
      <select id={`${id}-${key}`} value={value} onChange={(e) => set(Number(e.target.value))} className={fieldInput}>
        {priorities.map((p) => <option key={p} value={p}>{t("priority", { level: p })}</option>)}
      </select>
    </div>
  );
  const box = "space-y-3 rounded-lg border border-line p-4";

  function save(event: React.FormEvent) {
    event.preventDefault();
    void run(async () => {
      await putJson("/v1/settings/customer-voice", {
        anonymousEnabled: anonymous,
        severities,
        rootCauses: causes.map(({ key, name, active }) => ({ key, name, active })),
        inquiryPriority,
        suggestionPriority,
        happiness: { enabled, points, question, portalQuestion, labels: labels.length === points ? labels : undefined, askOnClosure, inPortal },
      }, csrfToken);
      router.refresh();
    });
  }

  return (
    <form onSubmit={save} className="max-w-3xl space-y-6" data-testid="voice-settings">
      <section className={box} aria-labelledby={`${id}-anonymous`}>
        <h2 id={`${id}-anonymous`} className="text-lg font-semibold">{t("anonymousHeading")}</h2>
        <label className="flex items-start gap-2">
          <input type="checkbox" checked={anonymous} onChange={(e) => setAnonymous(e.target.checked)} className="mt-1 h-5 w-5" />
          <span>{t("anonymous")}<span className="block text-sm text-ink-muted">{t("anonymousHint")}</span></span>
        </label>
      </section>

      <section className={box} aria-labelledby={`${id}-severities`}>
        <h2 id={`${id}-severities`} className="text-lg font-semibold">{t("severities")}</h2>
        <p className="text-sm text-ink-muted">{t("severitiesHint")}</p>
        {severities.map((s, i) => (
          <div key={i} className="grid gap-2 sm:grid-cols-[1fr_auto]">
            <BilingualInput label={t("severity", { level: i + 1 })} value={s.name} idPrefix={`${id}-sev${i}`}
              onChange={(name) => setSeverities(severities.map((x, j) => (j === i ? { ...x, name } : x)))} />
            {prioritySelect(t("slaPriority"), s.priority, (priority) => setSeverities(severities.map((x, j) => (j === i ? { ...x, priority } : x))), `sevp${i}`)}
          </div>
        ))}
        <div className="grid gap-2 sm:grid-cols-2">
          {prioritySelect(t("inquiryPriority"), inquiryPriority, setInquiryPriority, "inq")}
          {prioritySelect(t("suggestionPriority"), suggestionPriority, setSuggestionPriority, "sug")}
        </div>
      </section>

      <section className={box} aria-labelledby={`${id}-causes`}>
        <h2 id={`${id}-causes`} className="text-lg font-semibold">{t("rootCauses")}</h2>
        <p className="text-sm text-ink-muted">{t("rootCausesHint")}</p>
        <ul className="space-y-3">
          {causes.map((c, i) => (
            <li key={i} className="space-y-2 border-b border-line pb-3">
              <div className="flex flex-wrap items-end gap-3">
                <div className="space-y-1">
                  <label htmlFor={`${id}-key${i}`} className="block text-sm">{t("key")}</label>
                  <input id={`${id}-key${i}`} value={c.key} dir="ltr" readOnly={c.existing} pattern="[a-z0-9-]+" maxLength={50}
                    onChange={(e) => setCauses(causes.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))} className={fieldInput} />
                </div>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={c.active} onChange={(e) => setCauses(causes.map((x, j) => (j === i ? { ...x, active: e.target.checked } : x)))} className="h-5 w-5" />
                  {t("active")}
                </label>
                {!c.existing && <button type="button" className={fieldButton} onClick={() => setCauses(causes.filter((_, j) => j !== i))}>{t("remove")}</button>}
              </div>
              <BilingualInput label={t("name")} value={c.name} idPrefix={`${id}-cause${i}`} onChange={(name) => setCauses(causes.map((x, j) => (j === i ? { ...x, name } : x)))} />
            </li>
          ))}
        </ul>
        <button type="button" className={fieldButton} disabled={causes.length >= 30}
          onClick={() => setCauses([...causes, { key: "", name: { en: "", ar: "" }, active: true, existing: false }])}>{t("addCause")}</button>
      </section>

      <section className={box} aria-labelledby={`${id}-happiness`}>
        <h2 id={`${id}-happiness`} className="text-lg font-semibold">{t("happiness")}</h2>
        <p className="text-sm text-ink-muted">{t("happinessHint")}</p>
        <label className="flex items-center gap-2"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} className="h-5 w-5" />{t("happinessEnabled")}</label>
        <label className="flex items-center gap-2"><input type="checkbox" checked={askOnClosure} onChange={(e) => setAskOnClosure(e.target.checked)} className="h-5 w-5" />{t("askOnClosure")}</label>
        <label className="flex items-center gap-2"><input type="checkbox" checked={inPortal} onChange={(e) => setInPortal(e.target.checked)} className="h-5 w-5" />{t("inPortal")}</label>
        <fieldset className="flex flex-wrap gap-4">
          <legend className="text-sm font-semibold">{t("points")}</legend>
          {([3, 5] as const).map((p) => (
            <label key={p} className="flex items-center gap-2">
              <input type="radio" name={`${id}-points`} checked={points === p} className="h-5 w-5"
                onChange={() => { setPoints(p); if (labels.length !== p) setLabels([]); }} />
              {t(p === 3 ? "threeFaces" : "fivePoints")}
            </label>
          ))}
        </fieldset>
        {labels.length !== points && <p className="text-sm text-ink-muted">{t("defaultLabels")}</p>}
        <BilingualInput label={t("question")} value={question} onChange={setQuestion} idPrefix={`${id}-q`} />
        <BilingualInput label={t("portalQuestion")} value={portalQuestion} onChange={setPortalQuestion} idPrefix={`${id}-pq`} />
        {labels.length === points && labels.map((l, i) => (
          <BilingualInput key={i} label={t("label", { point: i + 1 })} value={l} idPrefix={`${id}-l${i}`} onChange={(v) => setLabels(labels.map((x, j) => (j === i ? v : x)))} />
        ))}
      </section>

      <button type="submit" className={primaryAction} disabled={busy}>{t("save")}</button>
      <VoiceStatusLine status={status} />
    </form>
  );
}

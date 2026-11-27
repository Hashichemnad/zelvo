"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { smallButton } from "@/components/directory/common";
import { buttonClass, inputClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { putJson } from "@/lib/client-api";
import { inLanguage, type Locale } from "@/lib/incidents";
import { maxFishboneCauses, maxWhySteps, type FishboneCategory, type Fishbone, type FiveWhys } from "@/lib/problems";
import { ProblemStatusLine, useSaver } from "./common";

/**
 * The root-cause tools of a problem (task H1): the 5 Whys (a statement, why / because steps and
 * the conclusion) and the fishbone (the effect and causes under the categories of Settings →
 * Problems). Plain form fields, so both work with the keyboard and screen readers; the fishbone
 * is also shown grouped by category as a list, not only as a drawing.
 */
export function ProblemAnalysisEditor({ problemId, etag, fiveWhys, fishbone, categories, canEdit, csrfToken }: {
  problemId: string;
  etag: string;
  fiveWhys: FiveWhys;
  fishbone: Fishbone;
  categories: FishboneCategory[];
  canEdit: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("problems.analysis");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [whys, setWhys] = useState<FiveWhys>(fiveWhys);
  const [bone, setBone] = useState<Fishbone>(fishbone);
  const categoryName = (key: string) => {
    const category = categories.find((c) => c.key === key);
    return category ? inLanguage(category.name, locale).value || key : key;
  };
  const choosable = categories.filter((c) => c.active || bone.causes.some((cause) => cause.category === c.key));

  if (!canEdit) {
    return (
      <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="problem-analysis">
        <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
        <h3 className="font-semibold">{t("fiveWhys")}</h3>
        {whys.statement && <p dir="auto">{whys.statement}</p>}
        <ol className="list-decimal space-y-1 ps-6">
          {whys.steps.map((s, i) => <li key={i} dir="auto">{s.why} — {s.because}</li>)}
        </ol>
        {whys.conclusion && <p dir="auto" className="font-semibold">{whys.conclusion}</p>}
        <h3 className="font-semibold">{t("fishbone")}</h3>
        <FishboneList fishbone={bone} name={categoryName} />
      </section>
    );
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="problem-analysis">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      <fieldset className="space-y-3">
        <legend className="font-semibold">{t("fiveWhys")}</legend>
        <label className="block space-y-1 text-sm">
          <span className="block">{t("statement")}</span>
          <input value={whys.statement ?? ""} maxLength={2000} dir="auto" className={inputClass}
            onChange={(e) => setWhys({ ...whys, statement: e.target.value })} />
        </label>
        <ol className="space-y-3">
          {whys.steps.map((step, i) => (
            <li key={i} className="space-y-2 rounded-md border border-line p-2">
              <p className="text-sm font-semibold">{t("step", { n: i + 1 })}</p>
              <div className="grid gap-2 sm:grid-cols-2">
                <label className="space-y-1 text-sm">
                  <span className="block">{t("why")}</span>
                  <input value={step.why} maxLength={2000} dir="auto" className={inputClass}
                    onChange={(e) => setWhys({ ...whys, steps: whys.steps.map((s, j) => (j === i ? { ...s, why: e.target.value } : s)) })} />
                </label>
                <label className="space-y-1 text-sm">
                  <span className="block">{t("because")}</span>
                  <input value={step.because ?? ""} maxLength={2000} dir="auto" className={inputClass}
                    onChange={(e) => setWhys({ ...whys, steps: whys.steps.map((s, j) => (j === i ? { ...s, because: e.target.value } : s)) })} />
                </label>
              </div>
              <button type="button" className={smallButton} onClick={() => setWhys({ ...whys, steps: whys.steps.filter((_, j) => j !== i) })}>
                {t("removeStep", { n: i + 1 })}
              </button>
            </li>
          ))}
        </ol>
        {whys.steps.length < maxWhySteps ? (
          <button type="button" className={smallButton} onClick={() => setWhys({ ...whys, steps: [...whys.steps, { why: "", because: "" }] })}>{t("addStep")}</button>
        ) : <p className="text-sm text-ink-muted">{t("maxSteps", { max: maxWhySteps })}</p>}
        <label className="block space-y-1 text-sm">
          <span className="block">{t("conclusion")}</span>
          <input value={whys.conclusion ?? ""} maxLength={2000} dir="auto" className={inputClass}
            onChange={(e) => setWhys({ ...whys, conclusion: e.target.value })} />
        </label>
      </fieldset>

      <fieldset className="space-y-3">
        <legend className="font-semibold">{t("fishbone")}</legend>
        <label className="block space-y-1 text-sm">
          <span className="block">{t("effect")}</span>
          <input value={bone.effect ?? ""} maxLength={1000} dir="auto" className={inputClass} onChange={(e) => setBone({ ...bone, effect: e.target.value })} />
        </label>
        {bone.causes.length === 0 && <p className="text-ink-muted">{t("noCauses")}</p>}
        <ul className="space-y-2">
          {bone.causes.map((cause, i) => (
            <li key={i} className="grid gap-2 rounded-md border border-line p-2 sm:grid-cols-[minmax(0,12rem)_minmax(0,1fr)_auto_auto] sm:items-end">
              <label className="space-y-1 text-sm">
                <span className="block">{t("causeCategory")}</span>
                <select value={cause.category} className={inputClass}
                  onChange={(e) => setBone({ ...bone, causes: bone.causes.map((c, j) => (j === i ? { ...c, category: e.target.value } : c)) })}>
                  {choosable.map((c) => <option key={c.key} value={c.key}>{categoryName(c.key)}</option>)}
                </select>
              </label>
              <label className="space-y-1 text-sm">
                <span className="block">{t("cause")}</span>
                <input value={cause.text} maxLength={1000} dir="auto" className={inputClass}
                  onChange={(e) => setBone({ ...bone, causes: bone.causes.map((c, j) => (j === i ? { ...c, text: e.target.value } : c)) })} />
              </label>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={cause.root} className="size-5 accent-brand"
                  onChange={(e) => setBone({ ...bone, causes: bone.causes.map((c, j) => (j === i ? { ...c, root: e.target.checked } : c)) })} />
                {t("root")}
              </label>
              <button type="button" className={smallButton} onClick={() => setBone({ ...bone, causes: bone.causes.filter((_, j) => j !== i) })}>
                {t("removeCause", { n: i + 1 })}
              </button>
            </li>
          ))}
        </ul>
        {bone.causes.length < maxFishboneCauses && choosable.some((c) => c.active) && (
          <button type="button" className={smallButton}
            onClick={() => setBone({ ...bone, causes: [...bone.causes, { category: choosable.find((c) => c.active)!.key, text: "", root: false }] })}>
            {t("addCause")}
          </button>
        )}
        <FishboneList fishbone={bone} name={categoryName} />
      </fieldset>

      <button type="button" className={buttonClass} disabled={busy || whys.steps.some((s) => !s.why.trim()) || bone.causes.some((c) => !c.text.trim())}
        onClick={() => void run(async () => {
          await putJson(`/v1/problems/${problemId}/analysis`, { fiveWhys: whys, fishbone: bone }, csrfToken, { "If-Match": etag });
          router.refresh();
        })}>
        {t("save")}
      </button>
      <ProblemStatusLine status={status} />
    </section>
  );
}

/** The fishbone as a list grouped by category: what a drawing shows, readable by everyone. */
function FishboneList({ fishbone, name }: { fishbone: Fishbone; name: (key: string) => string }) {
  const t = useTranslations("problems.analysis");
  const groups = [...new Set(fishbone.causes.filter((c) => c.text.trim()).map((c) => c.category))];
  if (groups.length === 0) return null;
  return (
    <div className="rounded-md bg-surface-muted p-3" data-testid="fishbone-list">
      {fishbone.effect && <p className="font-semibold" dir="auto">{t("effect")}: {fishbone.effect}</p>}
      <dl className="grid gap-2 sm:grid-cols-2">
        {groups.map((g) => (
          <div key={g}>
            <dt className="font-semibold">{name(g)}</dt>
            {fishbone.causes.filter((c) => c.category === g && c.text.trim()).map((c, i) => (
              <dd key={i} dir="auto" className={c.root ? "font-semibold" : undefined}>
                {c.text} {c.root && <span className="text-sm">{t("rootMark")}</span>}
              </dd>
            ))}
          </div>
        ))}
      </dl>
    </div>
  );
}

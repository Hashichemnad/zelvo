"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError, putJson } from "@/lib/client-api";
import type { IncidentSettings, NamedLevel, OnHoldReason } from "@/lib/incidents";

const inputClass =
  "w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";
const smallButton =
  "rounded-md border border-line px-3 py-1.5 text-sm font-semibold text-ink hover:bg-surface-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

type Names = { en: string; ar: string }[];

const toNames = (levels: NamedLevel[]): Names => levels.map((l) => ({ en: l.name.en ?? "", ar: l.name.ar ?? "" }));

/**
 * Settings → Incidents (module 1): the impact × urgency matrix (3 × 3 giving 5 priorities, decision
 * 2026-11-22), on-hold reasons, the number prefix and the auto-close period.
 */
export function IncidentSettingsForm({ initial, csrfToken }: { initial: IncidentSettings; csrfToken: string }) {
  const t = useTranslations("settings.incidents");
  const tCommon = useTranslations("common");
  const id = useId();
  const [impacts, setImpacts] = useState<Names>(toNames(initial.priorityMatrix.impacts));
  const [urgencies, setUrgencies] = useState<Names>(toNames(initial.priorityMatrix.urgencies));
  const [priorities, setPriorities] = useState<Names>(toNames(initial.priorityMatrix.priorities));
  const [matrix, setMatrix] = useState<number[][]>(initial.priorityMatrix.matrix.map((row) => [...row]));
  const [reasons, setReasons] = useState<{ key: string; en: string; ar: string; active: boolean; pausesSla: boolean }[]>(
    initial.onHoldReasons.map((r: OnHoldReason) => ({ key: r.key, en: r.name.en ?? "", ar: r.name.ar ?? "", active: r.active, pausesSla: r.pausesSla ?? false })),
  );
  const [prefix, setPrefix] = useState(initial.numberPrefixes.Incident ?? "INC");
  const [autoCloseDays, setAutoCloseDays] = useState(initial.autoCloseDays);
  const [businessDays, setBusinessDays] = useState(initial.autoCloseBusinessDays ?? false);
  const [status, setStatus] = useState<"idle" | "saving" | "saved" | "failed" | "inUse" | "rateLimited">("idle");

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setStatus("saving");
    const names = (list: Names) => list.map((n) => ({ en: n.en.trim() || null, ar: n.ar.trim() || null }));
    try {
      await putJson(
        "/v1/settings/incidents",
        {
          priorityMatrix: { impacts: names(impacts), urgencies: names(urgencies), priorities: names(priorities), matrix },
          onHoldReasons: reasons.map((r) => ({ key: r.key.trim(), name: { en: r.en.trim() || null, ar: r.ar.trim() || null }, active: r.active, pausesSla: r.pausesSla })),
          numberPrefixes: { Incident: prefix.trim().toUpperCase() },
          autoCloseDays,
          autoCloseBusinessDays: businessDays,
        },
        csrfToken,
      );
      setStatus("saved");
    } catch (error) {
      setStatus(error instanceof ApiError && error.rateLimited ? "rateLimited" : error instanceof ApiError && error.code === "on-hold-reason-in-use" ? "inUse" : "failed");
    }
  }

  const nameRows = (label: string, list: Names, set: (n: Names) => void, key: string) => (
    <fieldset className="space-y-2">
      <legend className="font-semibold">{label}</legend>
      {list.map((name, index) => (
        <div key={`${key}-${index}`} className="grid gap-2 sm:grid-cols-2">
          <div>
            <label htmlFor={`${id}-${key}-${index}-en`} className="block text-sm">
              {t("levelEn", { level: index + 1 })}
            </label>
            <input id={`${id}-${key}-${index}-en`} lang="en" dir="ltr" maxLength={100} value={name.en} className={inputClass}
              onChange={(e) => set(list.map((n, i) => (i === index ? { ...n, en: e.target.value } : n)))} />
          </div>
          <div>
            <label htmlFor={`${id}-${key}-${index}-ar`} className="block text-sm">
              {t("levelAr", { level: index + 1 })}
            </label>
            <input id={`${id}-${key}-${index}-ar`} lang="ar" dir="rtl" maxLength={100} value={name.ar} className={inputClass}
              onChange={(e) => set(list.map((n, i) => (i === index ? { ...n, ar: e.target.value } : n)))} />
          </div>
        </div>
      ))}
    </fieldset>
  );

  return (
    <form onSubmit={save} className="space-y-8" data-testid="incident-settings">
      <section className="space-y-4" aria-labelledby={`${id}-matrix`}>
        <h3 id={`${id}-matrix`} className="text-xl font-bold">
          {t("matrix")}
        </h3>
        <p className="max-w-2xl">{t("matrixIntro")}</p>
        {nameRows(t("impacts"), impacts, setImpacts, "impact")}
        {nameRows(t("urgencies"), urgencies, setUrgencies, "urgency")}
        {nameRows(t("priorities"), priorities, setPriorities, "priority")}
        <div className="overflow-x-auto">
          <table className="border-collapse text-sm">
            <caption className="pb-2 text-start font-semibold">{t("matrixTable")}</caption>
            <thead>
              <tr>
                <th scope="col" className="p-2 text-start">{t("impactUrgency")}</th>
                {urgencies.map((u, index) => (
                  <th key={index} scope="col" className="p-2 text-start">
                    {u.en || u.ar || index + 1}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {matrix.map((row, impact) => (
                <tr key={impact}>
                  <th scope="row" className="p-2 text-start">
                    {impacts[impact]?.en || impacts[impact]?.ar || impact + 1}
                  </th>
                  {row.map((priority, urgency) => (
                    <td key={urgency} className="p-2">
                      <select
                        aria-label={t("cell", { impact: impact + 1, urgency: urgency + 1 })}
                        value={priority}
                        className={inputClass}
                        onChange={(e) => setMatrix(matrix.map((r, i) => (i === impact ? r.map((p, j) => (j === urgency ? Number(e.target.value) : p)) : r)))}
                      >
                        {priorities.map((p, index) => (
                          <option key={index} value={index + 1}>
                            {p.en || p.ar || index + 1}
                          </option>
                        ))}
                      </select>
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section className="space-y-3" aria-labelledby={`${id}-reasons`}>
        <h3 id={`${id}-reasons`} className="text-xl font-bold">
          {t("reasons")}
        </h3>
        <p className="max-w-2xl">{t("reasonsIntro")}</p>
        {reasons.map((reason, index) => (
          <fieldset key={index} className="grid items-end gap-2 rounded-lg border border-line p-3 sm:grid-cols-[1fr_1fr_1fr_auto_auto_auto]">
            <legend className="sr-only">{reason.key}</legend>
            <div>
              <label htmlFor={`${id}-reason-${index}-key`} className="block text-sm">{t("reasonKey")}</label>
              <input id={`${id}-reason-${index}-key`} dir="ltr" value={reason.key} maxLength={50} className={inputClass}
                onChange={(e) => setReasons(reasons.map((r, i) => (i === index ? { ...r, key: e.target.value } : r)))} />
            </div>
            <div>
              <label htmlFor={`${id}-reason-${index}-en`} className="block text-sm">{t("reasonEn")}</label>
              <input id={`${id}-reason-${index}-en`} lang="en" dir="ltr" value={reason.en} maxLength={100} className={inputClass}
                onChange={(e) => setReasons(reasons.map((r, i) => (i === index ? { ...r, en: e.target.value } : r)))} />
            </div>
            <div>
              <label htmlFor={`${id}-reason-${index}-ar`} className="block text-sm">{t("reasonAr")}</label>
              <input id={`${id}-reason-${index}-ar`} lang="ar" dir="rtl" value={reason.ar} maxLength={100} className={inputClass}
                onChange={(e) => setReasons(reasons.map((r, i) => (i === index ? { ...r, ar: e.target.value } : r)))} />
            </div>
            <label className="flex items-center gap-2 pb-2">
              <input type="checkbox" checked={reason.active} onChange={(e) => setReasons(reasons.map((r, i) => (i === index ? { ...r, active: e.target.checked } : r)))} />
              {t("reasonActive")}
            </label>
            <label className="flex items-center gap-2 pb-2">
              <input type="checkbox" checked={reason.pausesSla} onChange={(e) => setReasons(reasons.map((r, i) => (i === index ? { ...r, pausesSla: e.target.checked } : r)))} />
              {t("reasonPausesSla")}
            </label>
            <button type="button" className={smallButton} onClick={() => setReasons(reasons.filter((_, i) => i !== index))}>
              {t("removeReason", { key: reason.key || index + 1 })}
            </button>
          </fieldset>
        ))}
        <button type="button" className={smallButton} onClick={() => setReasons([...reasons, { key: "", en: "", ar: "", active: true, pausesSla: false }])}>
          {t("addReason")}
        </button>
      </section>

      <section className="grid max-w-2xl gap-4 sm:grid-cols-2" aria-labelledby={`${id}-numbers`}>
        <h3 id={`${id}-numbers`} className="text-xl font-bold sm:col-span-2">
          {t("numbers")}
        </h3>
        <div className="space-y-1">
          <label htmlFor={`${id}-prefix`} className="block font-semibold">{t("prefix")}</label>
          <input id={`${id}-prefix`} dir="ltr" value={prefix} maxLength={10} onChange={(e) => setPrefix(e.target.value)} aria-describedby={`${id}-prefix-hint`} className={inputClass} />
          <p id={`${id}-prefix-hint`} className="text-sm text-ink-muted">{t("prefixHint")}</p>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-days`} className="block font-semibold">{t("autoClose")}</label>
          <input id={`${id}-days`} type="number" min={1} max={365} value={autoCloseDays} onChange={(e) => setAutoCloseDays(Number(e.target.value))} aria-describedby={`${id}-days-hint`} className={inputClass} />
          <p id={`${id}-days-hint`} className="text-sm text-ink-muted">{t("autoCloseHint")}</p>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={businessDays} onChange={(e) => setBusinessDays(e.target.checked)} />
            {t("autoCloseBusinessDays")}
          </label>
        </div>
      </section>

      <div className="flex flex-wrap items-center gap-4">
        <button type="submit" disabled={status === "saving"} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60">
          {status === "saving" ? t("saving") : t("save")}
        </button>
        <p role="status" aria-live="polite" className={status === "saved" ? "text-ok" : "text-bad"}>
          {status === "saved" ? t("saved") : status === "failed" ? t("failed") : status === "inUse" ? t("inUse") : status === "rateLimited" ? tCommon("rateLimited") : ""}
        </p>
      </div>
    </form>
  );
}

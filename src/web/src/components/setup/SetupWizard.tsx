"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { SaveStatusLine, useSaver } from "@/components/directory/common";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { Link } from "@/i18n/navigation";
import { getJson, patchJson, postJson, putJson } from "@/lib/client-api";
import { formatFigure, setupStepLinks, type DepartmentTemplate, type SetupState, type SetupStepKey, type SetupStepStatus } from "@/lib/reports";
import type { CalendarSystem, DigitSystem } from "@/lib/format";
import type { LicenseState } from "@/lib/license";
import { LicensePanel } from "@/components/license/LicensePanel";
import { TemplatePicker } from "./TemplatePicker";

/**
 * The setup wizard (module 10, task F2): the steps that make a new organisation live in under a
 * working day. Each step says what to do in plain words, shows whether its settings already look
 * set up, links to its own Settings page and is marked done or skipped. The organisation's name is
 * changed here; the departments step creates whole service desks from templates.
 */
export function SetupWizard({ initial, templates, tenantName, canRename, csrfToken, digits, license = null, calendar = "gregory", timeZone = "Asia/Dubai" }: {
  initial: SetupState;
  templates: DepartmentTemplate[];
  tenantName: { en: string | null; ar: string | null } | null;
  canRename: boolean;
  csrfToken: string;
  digits: DigitSystem;
  /** Task F3: the license, for administrators who may activate the installation. */
  license?: LicenseState | null;
  calendar?: CalendarSystem;
  timeZone?: string;
}) {
  const t = useTranslations("setup");
  const locale = useLocale();
  const id = useId();
  const [state, setState] = useState(initial);
  const firstOpen = initial.steps.find((s) => s.status === "Pending")?.key ?? initial.steps[0]?.key ?? "license";
  const [current, setCurrent] = useState<SetupStepKey>(firstOpen);
  const { status, run, busy } = useSaver();
  const [name, setName] = useState({ en: tenantName?.en ?? "", ar: tenantName?.ar ?? "" });
  const n = (v: number) => formatFigure(v, { locale, calendar: "gregory", digits });
  const step = state.steps.find((s) => s.key === current) ?? state.steps[0]!;
  const index = state.steps.findIndex((s) => s.key === step.key);

  async function mark(statusValue: SetupStepStatus) {
    const next = await run(() => putJson<SetupState>(`/v1/setup/steps/${step.key}`, { status: statusValue }, csrfToken));
    if (next) {
      setState(next);
      if (statusValue !== "Pending") {
        const following = next.steps.slice(index + 1).find((s) => s.status === "Pending") ?? next.steps.find((s) => s.status === "Pending");
        if (following) setCurrent(following.key);
      }
    }
  }

  async function finish(reopen: boolean) {
    const next = await run(() => postJson<SetupState>(reopen ? "/v1/setup/reopen" : "/v1/setup/finish", undefined, csrfToken));
    if (next) setState(next);
  }

  async function rename() {
    const saved = await run(() => patchJson("/v1/tenant", { name: { en: name.en.trim() || null, ar: name.ar.trim() || null } }, csrfToken));
    if (saved) setState(await getJson<SetupState>("/v1/setup"));
  }

  const link = setupStepLinks[step.key];
  const percent = state.total === 0 ? 0 : Math.round((state.finished / state.total) * 100);

  return (
    <div className="space-y-6" data-testid="setup-wizard">
      <div className="space-y-1">
        <p id={`${id}-progress`} className="font-semibold">{t("progress", { done: n(state.finished), total: n(state.total) })}</p>
        <div role="progressbar" aria-labelledby={`${id}-progress`} aria-valuemin={0} aria-valuemax={state.total} aria-valuenow={state.finished}
          aria-valuetext={t("progress", { done: n(state.finished), total: n(state.total) })} className="h-3 rounded-full bg-surface-muted">
          <div className="h-3 rounded-full bg-brand" style={{ width: `${percent}%` }} />
        </div>
        {state.completedAt && <p className="text-ok" data-testid="setup-finished">{t("finished")}</p>}
      </div>

      <div className="grid gap-6 md:grid-cols-[16rem_1fr]">
        <nav aria-label={t("stepsLabel")}>
          <ol className="space-y-1">
            {state.steps.map((s, i) => (
              <li key={s.key}>
                <button type="button" onClick={() => setCurrent(s.key)} aria-current={s.key === step.key ? "step" : undefined}
                  className={`flex w-full items-center gap-2 rounded-md px-3 py-2 text-start text-sm font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand ${s.key === step.key ? "bg-brand text-surface" : "hover:bg-surface-muted"}`}>
                  <span aria-hidden="true" className="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-full border border-current text-xs">
                    {s.status === "Done" ? "✓" : s.status === "Skipped" ? "–" : n(i + 1)}
                  </span>
                  <span>{t(`steps.${s.key}.title`)}</span>
                  <span className="sr-only">: {t(`status.${s.status}`)}</span>
                </button>
              </li>
            ))}
          </ol>
        </nav>

        <section aria-labelledby={`${id}-step`} className="space-y-4 rounded-lg border border-line bg-surface p-4" data-testid={`setup-step-${step.key}`}>
          <p className="text-sm text-ink-muted">{t("stepOf", { step: n(index + 1), total: n(state.total) })} · {t(`status.${step.status}`)}</p>
          <h2 id={`${id}-step`} className="text-2xl font-bold">{t(`steps.${step.key}.title`)}</h2>
          <p className="max-w-2xl">{t(`steps.${step.key}.description`)}</p>
          {step.configured !== null && (
            <p className={step.configured ? "text-ok" : "text-warn"} data-testid="setup-check">{t(step.configured ? "configured" : "notConfigured")}</p>
          )}

          {step.key === "organisation" && (canRename ? (
            <form className="max-w-xl space-y-2" onSubmit={(e) => { e.preventDefault(); void rename(); }}>
              <fieldset className="grid gap-2 sm:grid-cols-2">
                <legend className="text-sm font-semibold">{t("organisationName")}</legend>
                <div className="space-y-1">
                  <label htmlFor={`${id}-org-en`} className="block text-sm">{t("templates.english")}</label>
                  <input id={`${id}-org-en`} lang="en" dir="ltr" maxLength={200} value={name.en} onChange={(e) => setName({ ...name, en: e.target.value })} className={fieldInput} />
                </div>
                <div className="space-y-1">
                  <label htmlFor={`${id}-org-ar`} className="block text-sm">{t("templates.arabic")}</label>
                  <input id={`${id}-org-ar`} lang="ar" dir="rtl" maxLength={200} value={name.ar} onChange={(e) => setName({ ...name, ar: e.target.value })} className={fieldInput} />
                </div>
              </fieldset>
              <button type="submit" disabled={busy || (!name.en.trim() && !name.ar.trim())} className={fieldButton}>{t("saveName")}</button>
            </form>
          ) : <p className="text-sm">{t("noRename")}</p>)}

          {step.key === "license" && (license ? (
            <LicensePanel initial={license} csrfToken={csrfToken} calendar={calendar} digits={digits} timeZone={timeZone} compact />
          ) : <p className="text-sm">{t("licenseByOperator")}</p>)}

          {step.key === "departments" && (
            <TemplatePicker templates={templates} csrfToken={csrfToken} digits={digits}
              onApplied={() => void getJson<SetupState>("/v1/setup").then(setState).catch(() => undefined)} />
          )}

          {link && (
            <p>
              <Link href={link} className="font-semibold text-brand underline-offset-4 hover:underline" data-testid="setup-step-link">{t(`steps.${step.key}.link`)}</Link>
            </p>
          )}

          <div className="flex flex-wrap gap-3 border-t border-line pt-4">
            {step.status === "Pending" ? (
              <>
                <button type="button" className={primaryAction} disabled={busy} onClick={() => void mark("Done")} data-testid="setup-done">{t("markDone")}</button>
                <button type="button" className={fieldButton} disabled={busy} onClick={() => void mark("Skipped")} data-testid="setup-skip">{t("skip")}</button>
              </>
            ) : (
              <button type="button" className={fieldButton} disabled={busy} onClick={() => void mark("Pending")}>{t("markPending")}</button>
            )}
          </div>
          <SaveStatusLine status={status} />
        </section>
      </div>

      <div className="flex flex-wrap items-center gap-3">
        {state.completedAt ? (
          <button type="button" className={fieldButton} disabled={busy} onClick={() => void finish(true)} data-testid="setup-reopen">{t("reopen")}</button>
        ) : (
          <>
            <button type="button" className={primaryAction} disabled={busy} onClick={() => void finish(false)} data-testid="setup-finish">{t("finish")}</button>
            <span className="text-sm text-ink-muted">{t("finishHint")}</span>
          </>
        )}
      </div>
    </div>
  );
}

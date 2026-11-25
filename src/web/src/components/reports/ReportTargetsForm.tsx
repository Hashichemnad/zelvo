"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { SaveStatusLine, useSaver } from "@/components/directory/common";
import { fieldInput, primaryAction } from "@/components/forms/ui";
import { putJson } from "@/lib/client-api";
import type { ReportTargets } from "@/lib/reports";

/** Settings → Reports (task F2): the targets the dashboards compare SLA compliance and happiness with. */
export function ReportTargetsForm({ targets, csrfToken }: { targets: ReportTargets; csrfToken: string }) {
  const t = useTranslations("reports.settings");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [sla, setSla] = useState(String(targets.slaTargetPercent));
  const [happiness, setHappiness] = useState(String(targets.happinessTargetPercent));
  const valid = (v: string) => Number(v) >= 1 && Number(v) <= 100;

  return (
    <form className="max-w-xl space-y-4" data-testid="report-targets"
      onSubmit={(e) => { e.preventDefault(); if (valid(sla) && valid(happiness)) void run(() => putJson("/v1/settings/reports", { slaTargetPercent: Number(sla), happinessTargetPercent: Number(happiness) }, csrfToken)); }}>
      <div className="space-y-1">
        <label htmlFor={`${id}-sla`} className="block font-semibold">{t("sla")}</label>
        <input id={`${id}-sla`} type="number" min={1} max={100} step={0.1} value={sla} onChange={(e) => setSla(e.target.value)} aria-describedby={`${id}-sla-hint`} aria-invalid={!valid(sla)} className={fieldInput} />
        <p id={`${id}-sla-hint`} className="text-sm text-ink-muted">{t("slaHint")}</p>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-happiness`} className="block font-semibold">{t("happiness")}</label>
        <input id={`${id}-happiness`} type="number" min={1} max={100} step={0.1} value={happiness} onChange={(e) => setHappiness(e.target.value)} aria-describedby={`${id}-happiness-hint`} aria-invalid={!valid(happiness)} className={fieldInput} />
        <p id={`${id}-happiness-hint`} className="text-sm text-ink-muted">{t("happinessHint")}</p>
      </div>
      {(!valid(sla) || !valid(happiness)) && <p role="alert" className="text-bad">{t("invalid")}</p>}
      <button type="submit" disabled={busy} className={primaryAction}>{t("save")}</button>
      <SaveStatusLine status={status} />
    </form>
  );
}

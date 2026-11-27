"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { primaryButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { Link } from "@/i18n/navigation";
import { putJson } from "@/lib/client-api";
import type { ProblemDetectionSettings } from "@/lib/problems";
import { ProblemStatusLine, useSaver } from "./common";

const inRange = (value: number, min: number, max: number) => Number.isFinite(value) && value >= min && value <= max;

/**
 * Settings → Problems → Problem detection (task H2): suggestions from similar incidents (needs the
 * AI feature, set up in Settings → AI) and from rising trends (decision 10: at least 10 incidents in
 * 30 days and twice the average of the three periods before).
 */
export function ProblemDetectionSettingsForm({ initial, csrfToken }: { initial: ProblemDetectionSettings; csrfToken: string }) {
  const t = useTranslations("problems.detection");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [value, setValue] = useState(initial);
  const set = (change: Partial<ProblemDetectionSettings>) => setValue({ ...value, ...change });
  const valid = inRange(value.windowDays, 30, 90) && Number.isInteger(value.windowDays) && inRange(value.minimumIncidents, 3, 100) && Number.isInteger(value.minimumIncidents)
    && inRange(value.trendMinimum, 2, 10000) && Number.isInteger(value.trendMinimum) && inRange(value.trendFactor, 1.1, 10);
  const number = (label: string, field: "windowDays" | "minimumIncidents" | "trendMinimum" | "trendFactor", min: number, max: number, step = 1, help?: string) => (
    <div className="space-y-1">
      <label className="block space-y-1">
        <span className="block font-semibold">{label}</span>
        <input type="number" dir="ltr" min={min} max={max} step={step} value={Number.isNaN(value[field]) ? "" : value[field]}
          onChange={(e) => set({ [field]: e.target.valueAsNumber } as Partial<ProblemDetectionSettings>)} className={`${fieldInput} max-w-40`}
          aria-describedby={help ? `${id}-${field}-help` : undefined} />
      </label>
      {help && <p id={`${id}-${field}-help`} className="text-sm text-ink-muted">{help}</p>}
    </div>
  );

  return (
    <form className="space-y-4 border-t border-line pt-6" aria-labelledby={`${id}-heading`} onSubmit={(e) => {
      e.preventDefault();
      if (!valid) return;
      const body = {
        similarEnabled: value.similarEnabled, windowDays: value.windowDays, minimumIncidents: value.minimumIncidents,
        trendsEnabled: value.trendsEnabled, trendMinimum: value.trendMinimum, trendFactor: value.trendFactor,
      };
      void run(async () => setValue(await putJson<ProblemDetectionSettings>("/v1/settings/problems/detection", body, csrfToken)));
    }}>
      <h3 id={`${id}-heading`} className="text-xl font-bold">{t("title")}</h3>
      <p className="max-w-2xl">{t("intro")}</p>
      <fieldset className="space-y-3">
        <legend className="font-semibold">{t("similar")}</legend>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={value.similarEnabled} onChange={(e) => set({ similarEnabled: e.target.checked })} className="size-5 accent-brand" />
          {t("similarEnabled")}
        </label>
        {!value.aiAvailable && (
          <p className="text-sm">
            {t.rich(value.aiReason === "limit-reached" ? "aiLimit" : "aiOff", {
              link: (chunks) => <Link href="/settings/ai/features" className="font-semibold text-brand underline underline-offset-4">{chunks}</Link>,
            })}
          </p>
        )}
        {number(t("windowDays"), "windowDays", 30, 90, 1, t("windowDaysHelp"))}
        {number(t("minimumIncidents"), "minimumIncidents", 3, 100)}
      </fieldset>
      <fieldset className="space-y-3">
        <legend className="font-semibold">{t("trends")}</legend>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={value.trendsEnabled} onChange={(e) => set({ trendsEnabled: e.target.checked })} className="size-5 accent-brand" />
          {t("trendsEnabled")}
        </label>
        {number(t("trendMinimum"), "trendMinimum", 2, 10000, 1, t("trendMinimumHelp"))}
        {number(t("trendFactor"), "trendFactor", 1.1, 10, 0.1, t("trendFactorHelp"))}
      </fieldset>
      <button type="submit" className={primaryButton} disabled={busy || !valid}>{t("save")}</button>
      <ProblemStatusLine status={status} />
    </form>
  );
}

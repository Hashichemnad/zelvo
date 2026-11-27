"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { primaryButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { putJson } from "@/lib/client-api";
import type { CmdbSettings } from "@/lib/cmdb";
import { CmdbStatusLine, useSaver } from "./common";

const prefixPattern = /^[A-Z][A-Z0-9]{0,9}$/;

/**
 * Settings → CMDB: the prefix of new item numbers (CI-000123; existing numbers keep theirs) and,
 * from task G2, the precedence of values people set against those of discovery sources.
 */
export function CmdbPrefixForm({ initial, csrfToken, canManage }: { initial: CmdbSettings; csrfToken: string; canManage: boolean }) {
  const t = useTranslations("cmdb.settings");
  const id = useId();
  const [prefix, setPrefix] = useState(initial.numberPrefix);
  const [manual, setManual] = useState(String(initial.manualPriority ?? 10));
  const { status, run, busy } = useSaver();
  const manualNumber = Number(manual);
  const manualInvalid = !Number.isInteger(manualNumber) || manualNumber < 1 || manualNumber > 100;
  const invalid = !prefixPattern.test(prefix) || manualInvalid;
  return (
    <form className="space-y-2" aria-labelledby={`${id}-label`}
      onSubmit={(e) => {
        e.preventDefault();
        if (!invalid) void run(() => putJson<CmdbSettings>("/v1/cmdb/settings", { numberPrefix: prefix, manualPriority: manualNumber }, csrfToken));
      }}>
      <label id={`${id}-label`} htmlFor={`${id}-prefix`} className="block font-semibold">{t("prefix")}</label>
      <input id={`${id}-prefix`} dir="ltr" value={prefix} maxLength={10} readOnly={!canManage} aria-describedby={`${id}-hint`} aria-invalid={invalid || undefined}
        onChange={(e) => setPrefix(e.target.value.toUpperCase())} className={`${fieldInput} max-w-40`} />
      <p id={`${id}-hint`} className={`text-sm ${!prefixPattern.test(prefix) ? "font-semibold text-bad" : "text-ink-muted"}`}>{t("prefixHint", { example: `${prefix || "CI"}-000123` })}</p>
      <label htmlFor={`${id}-manual`} className="block font-semibold">{t("manualPriority")}</label>
      <input id={`${id}-manual`} type="number" inputMode="numeric" min={1} max={100} dir="ltr" value={manual} readOnly={!canManage} aria-describedby={`${id}-manual-hint`}
        aria-invalid={manualInvalid || undefined} onChange={(e) => setManual(e.target.value)} className={`${fieldInput} max-w-28`} />
      <p id={`${id}-manual-hint`} className={`max-w-2xl text-sm ${manualInvalid ? "font-semibold text-bad" : "text-ink-muted"}`}>{t("manualPriorityHint")}</p>
      {canManage && <button type="submit" className={primaryButton} disabled={busy || invalid}>{t("save")}</button>}
      <CmdbStatusLine status={status} />
    </form>
  );
}

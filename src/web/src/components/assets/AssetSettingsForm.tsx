"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { fieldInput } from "@/components/forms/ui";
import { primaryButton } from "@/components/settings/integrations/ui";
import { putJson } from "@/lib/client-api";
import type { AssetSettings } from "@/lib/assets";
import { CmdbStatusLine, useSaver } from "@/components/cmdb/common";

const prefixPattern = /^[A-Z][A-Z0-9]{0,9}$/;

/** Settings → Assets (task G3): the prefix of generated asset tags and how many days before an end date alerts go out. */
export function AssetSettingsForm({ initial, csrfToken, canManage }: { initial: AssetSettings; csrfToken: string; canManage: boolean }) {
  const t = useTranslations("assets.settings");
  const id = useId();
  const [prefix, setPrefix] = useState(initial.tagPrefix);
  const [days, setDays] = useState(String(initial.expiryWarningDays));
  const { status, run, busy } = useSaver();
  const dayNumber = Number(days);
  const daysInvalid = !Number.isInteger(dayNumber) || dayNumber < 1 || dayNumber > 365;
  const prefixInvalid = !prefixPattern.test(prefix);
  return (
    <form className="max-w-2xl space-y-3" aria-label={t("title")}
      onSubmit={(e) => {
        e.preventDefault();
        if (!daysInvalid && !prefixInvalid) void run(() => putJson<AssetSettings>("/v1/assets/settings", { tagPrefix: prefix, expiryWarningDays: dayNumber }, csrfToken));
      }}>
      <div className="space-y-1">
        <label htmlFor={`${id}-prefix`} className="block font-semibold">{t("prefix")}</label>
        <input id={`${id}-prefix`} dir="ltr" maxLength={10} value={prefix} readOnly={!canManage} aria-describedby={`${id}-prefix-hint`} aria-invalid={prefixInvalid || undefined}
          onChange={(e) => setPrefix(e.target.value.toUpperCase())} className={`${fieldInput} max-w-40`} />
        <p id={`${id}-prefix-hint`} className={`text-sm ${prefixInvalid ? "font-semibold text-bad" : "text-ink-muted"}`}>{t("prefixHint", { example: `${prefix || "AST"}-000123` })}</p>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-days`} className="block font-semibold">{t("days")}</label>
        <input id={`${id}-days`} type="number" min={1} max={365} dir="ltr" value={days} readOnly={!canManage} aria-describedby={`${id}-days-hint`} aria-invalid={daysInvalid || undefined}
          onChange={(e) => setDays(e.target.value)} className={`${fieldInput} max-w-28`} />
        <p id={`${id}-days-hint`} className={`text-sm ${daysInvalid ? "font-semibold text-bad" : "text-ink-muted"}`}>{t("daysHint")}</p>
      </div>
      {canManage && <button type="submit" className={primaryButton} disabled={busy || daysInvalid || prefixInvalid}>{t("save")}</button>}
      <CmdbStatusLine status={status} />
    </form>
  );
}

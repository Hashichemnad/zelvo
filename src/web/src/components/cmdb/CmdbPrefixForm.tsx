"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { primaryButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { putJson } from "@/lib/client-api";
import type { CmdbSettings } from "@/lib/cmdb";
import { CmdbStatusLine, useSaver } from "./common";

const prefixPattern = /^[A-Z][A-Z0-9]{0,9}$/;

/** Settings → CMDB: the prefix of new item numbers (CI-000123); existing numbers keep theirs. */
export function CmdbPrefixForm({ initial, csrfToken, canManage }: { initial: CmdbSettings; csrfToken: string; canManage: boolean }) {
  const t = useTranslations("cmdb.settings");
  const id = useId();
  const [prefix, setPrefix] = useState(initial.numberPrefix);
  const { status, run, busy } = useSaver();
  const invalid = !prefixPattern.test(prefix);
  return (
    <form className="space-y-2" aria-labelledby={`${id}-label`}
      onSubmit={(e) => {
        e.preventDefault();
        if (!invalid) void run(() => putJson<CmdbSettings>("/v1/cmdb/settings", { numberPrefix: prefix }, csrfToken));
      }}>
      <label id={`${id}-label`} htmlFor={`${id}-prefix`} className="block font-semibold">{t("prefix")}</label>
      <input id={`${id}-prefix`} dir="ltr" value={prefix} maxLength={10} readOnly={!canManage} aria-describedby={`${id}-hint`} aria-invalid={invalid || undefined}
        onChange={(e) => setPrefix(e.target.value.toUpperCase())} className={`${fieldInput} max-w-40`} />
      <p id={`${id}-hint`} className={`text-sm ${invalid ? "font-semibold text-bad" : "text-ink-muted"}`}>{t("prefixHint", { example: `${prefix || "CI"}-000123` })}</p>
      {canManage && <button type="submit" className={primaryButton} disabled={busy || invalid}>{t("save")}</button>}
      <CmdbStatusLine status={status} />
    </form>
  );
}

"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { ApiError, putJson } from "@/lib/client-api";
import type { KnowledgeSettings } from "@/lib/knowledge";
import { inputClass, primaryButton } from "./integrations/ui";

/** Settings → Knowledge: public reading of external articles and the number prefix. */
export function KnowledgeSettingsForm({ initial, csrfToken }: { initial: KnowledgeSettings; csrfToken: string }) {
  const t = useTranslations("settings.knowledge");
  const id = useId();
  const [publicAccess, setPublicAccess] = useState(initial.publicAccess);
  const [prefix, setPrefix] = useState(initial.numberPrefix);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    try {
      await putJson("/v1/settings/knowledge", { publicAccess, numberPrefix: prefix }, csrfToken);
      setMessage({ ok: true, text: t("saved") });
    } catch (refused) {
      setMessage({ ok: false, text: refused instanceof ApiError && refused.status === 400 ? t("prefixInvalid") : t("failed") });
    }
  }

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-4" noValidate data-testid="knowledge-settings">
      <label className="flex items-start gap-2">
        <input type="checkbox" checked={publicAccess} onChange={(e) => setPublicAccess(e.target.checked)} className="mt-1" aria-describedby={`${id}-public-help`} />
        <span>
          <span className="font-semibold">{t("publicAccess")}</span>
          <span id={`${id}-public-help`} className="block text-sm text-ink-muted">{t("publicAccessHelp")}</span>
        </span>
      </label>
      <div>
        <label htmlFor={`${id}-prefix`} className="font-semibold">{t("numberPrefix")}</label>
        <input id={`${id}-prefix`} dir="ltr" maxLength={10} value={prefix} onChange={(e) => setPrefix(e.target.value.toUpperCase())} aria-describedby={`${id}-prefix-help`} className={inputClass} />
        <p id={`${id}-prefix-help`} className="text-sm text-ink-muted">{t("numberPrefixHelp")}</p>
      </div>
      {message && <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-bad"}>{message.text}</p>}
      <button type="submit" className={primaryButton}>{t("save")}</button>
    </form>
  );
}

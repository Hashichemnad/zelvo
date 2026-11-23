"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { codeClass, primaryButton, secondaryButton } from "./ui";

/**
 * A key or secret shown once, right after it was created or replaced: copy button, the warning
 * that it cannot be shown again, and a button to close it.
 */
export function SecretReveal({ label, value, onDone }: { label: string; value: string; onDone: () => void }) {
  const t = useTranslations("settings.integrations.secret");
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <div role="alert" aria-labelledby="secret-title" className="space-y-3 rounded-md border-2 border-brand p-4" data-testid="secret-reveal">
      <h3 id="secret-title" className="text-lg font-semibold">
        {t("title")}
      </h3>
      <p>{t("warning")}</p>
      <p className="font-semibold">{label}</p>
      <code dir="ltr" lang="en" className={codeClass} data-testid="secret-value">
        {value}
      </code>
      <div className="flex flex-wrap items-center gap-3">
        <button type="button" onClick={copy} className={secondaryButton}>
          {t("copy")}
        </button>
        <button type="button" onClick={onDone} className={primaryButton}>
          {t("done")}
        </button>
        <span role="status" aria-live="polite" className="text-ok">
          {copied ? t("copied") : ""}
        </span>
      </div>
    </div>
  );
}

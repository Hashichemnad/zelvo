"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError } from "@/lib/client-api";

export { cellClass, dangerButton, inputClass, primaryButton, secondaryButton, tableClass } from "@/components/settings/integrations/ui";

export const smallButton =
  "rounded-md border border-line px-2 py-1 text-sm font-semibold text-ink hover:bg-surface-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60";

export type SaveStatus = { kind: "idle" | "saving" | "saved" } | { kind: "failed"; code: string | null };

/** Runs one change at a time and remembers how it went, for a status line under the form. */
export function useSaver() {
  const [status, setStatus] = useState<SaveStatus>({ kind: "idle" });
  async function run<T>(action: () => Promise<T>): Promise<T | undefined> {
    setStatus({ kind: "saving" });
    try {
      const result = await action();
      setStatus({ kind: "saved" });
      return result;
    } catch (error) {
      setStatus({ kind: "failed", code: error instanceof ApiError ? (error.rateLimited ? "rate-limited" : (error.code ?? `status-${error.status}`)) : null });
      return undefined;
    }
  }
  return { status, run, busy: status.kind === "saving", reset: () => setStatus({ kind: "idle" }) };
}

/** The result of the last change, read out by screen readers (a polite live region). */
export function SaveStatusLine({ status, id }: { status: SaveStatus; id?: string }) {
  const t = useTranslations("directory");
  const tCommon = useTranslations("common");
  let text = "";
  if (status.kind === "saving") text = t("saving");
  else if (status.kind === "saved") text = t("saved");
  else if (status.kind === "failed") {
    const code = status.code;
    text = code === "rate-limited" ? tCommon("rateLimited") : code && t.has(`errors.${code}`) ? t(`errors.${code}`) : t("failed");
  }
  return (
    <p id={id} role="status" aria-live="polite" className={status.kind === "failed" ? "text-bad" : "text-ok"}>
      {text}
    </p>
  );
}

/** A name in English and Arabic: two labelled inputs, each in its own direction. */
export function BilingualInputs({
  id,
  label,
  en,
  ar,
  onChange,
  maxLength = 100,
  describedBy,
}: {
  id: string;
  label: string;
  en: string;
  ar: string;
  onChange: (value: { en: string; ar: string }) => void;
  maxLength?: number;
  describedBy?: string;
}) {
  const t = useTranslations("directory");
  return (
    <fieldset className="grid gap-2 sm:grid-cols-2">
      <legend className="font-semibold">{label}</legend>
      <div className="space-y-1">
        <label htmlFor={`${id}-en`} className="block text-sm">
          {t("inEnglish")}
        </label>
        <input id={`${id}-en`} lang="en" dir="ltr" value={en} maxLength={maxLength} aria-describedby={describedBy}
          onChange={(e) => onChange({ en: e.target.value, ar })} className={inputClassLocal} />
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-ar`} className="block text-sm">
          {t("inArabic")}
        </label>
        <input id={`${id}-ar`} lang="ar" dir="rtl" value={ar} maxLength={maxLength} aria-describedby={describedBy}
          onChange={(e) => onChange({ en, ar: e.target.value })} className={inputClassLocal} />
      </div>
    </fieldset>
  );
}

const inputClassLocal =
  "block w-full rounded-md border border-line bg-surface px-3 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

/** A bilingual value shown in the page's language, marked with lang and dir when it falls back to the other. */
export function Localized({ text, locale }: { text: { en: string | null; ar: string | null } | null | undefined; locale: "en" | "ar" }) {
  const other = locale === "ar" ? "en" : "ar";
  const own = text?.[locale];
  if (own) return <>{own}</>;
  const fallback = text?.[other];
  return fallback ? (
    <span lang={other} dir={other === "ar" ? "rtl" : "ltr"}>
      {fallback}
    </span>
  ) : (
    <>—</>
  );
}

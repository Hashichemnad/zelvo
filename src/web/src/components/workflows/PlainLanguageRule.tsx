"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { inputClass, primaryButton } from "@/components/directory/common";
import { ApiError, postJson } from "@/lib/client-api";
import type { PlainLanguageAvailability, WorkflowRule } from "@/lib/workflows";

const maxLength = 2000;

/**
 * Plain-language rules (module 20, task K1): the administrator describes a rule in English or
 * Arabic and the AI makes a draft from the tenant's own lists. The draft does not run until it is
 * approved below, in the list of rules.
 */
export function PlainLanguageRule({ availability, csrfToken, onDrafted }: {
  availability: PlainLanguageAvailability;
  csrfToken: string;
  onDrafted: (rule: WorkflowRule) => void;
}) {
  const t = useTranslations("workflows.plainLanguage");
  const id = useId();
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ code: string; unmapped: string[] } | null>(null);

  async function build() {
    setBusy(true);
    setError(null);
    try {
      const draft = await postJson<WorkflowRule>("/v1/workflow-rules/plain-language", { text }, csrfToken);
      setText("");
      onDrafted(draft);
    } catch (e) {
      const unmapped = e instanceof ApiError && Array.isArray(e.details.unmapped) ? (e.details.unmapped as string[]) : [];
      setError({ code: e instanceof ApiError ? (e.code ?? "failed") : "failed", unmapped });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="plain-language-rule">
      <h3 id={`${id}-heading`} className="text-lg font-semibold">{t("title")}</h3>
      <p className="max-w-2xl">{t("intro")}</p>
      {!availability.available ? (
        <p role="status" className="text-ink-muted">{t(availability.reason === "limit-reached" ? "limitReached" : "off")}</p>
      ) : (
        <form className="space-y-2" onSubmit={(e) => { e.preventDefault(); void build(); }}>
          <label htmlFor={`${id}-text`} className="block font-semibold">{t("label")}</label>
          <textarea id={`${id}-text`} dir="auto" rows={3} required maxLength={maxLength} className={inputClass} value={text}
            aria-describedby={`${id}-hint`} onChange={(e) => setText(e.target.value)} />
          <p id={`${id}-hint`} className="text-sm text-ink-muted">{t("hint")}</p>
          <button type="submit" className={primaryButton} disabled={busy || text.trim().length === 0}>{busy ? t("building") : t("build")}</button>
          <p aria-live="polite" className="sr-only">{busy ? t("building") : ""}</p>
        </form>
      )}
      {error && (
        <div role="alert" className="space-y-1 rounded-md border border-bad p-3" data-testid="plain-language-error">
          <p className="font-semibold">{t.has(`errors.${error.code}`) ? t(`errors.${error.code}`) : t("errors.failed")}</p>
          {error.unmapped.length > 0 && (
            <ul className="list-disc ps-5" lang="en" dir="ltr">
              {error.unmapped.map((u, i) => <li key={i}>{u}</li>)}
            </ul>
          )}
        </div>
      )}
    </section>
  );
}

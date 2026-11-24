"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError, postJson } from "@/lib/client-api";
import { aiErrorKey, type AiTranslation } from "@/lib/ai-features";

/**
 * One-click translation of a message (LANG-7, task E5): shows the AI's translation into the
 * reader's language under the message, marked as a machine translation. The original stays as
 * written. Translations are cached by the API, so asking again costs nothing.
 */
export function TranslateButton({
  path,
  language,
  csrfToken,
  body = {},
}: {
  /** The API path that translates this message (POST). */
  path: string;
  /** Language to translate into. */
  language: "en" | "ar";
  csrfToken: string;
  /** Extra fields of the request, such as the comment id. */
  body?: Record<string, unknown>;
}) {
  const t = useTranslations("ai.assist");
  const [state, setState] = useState<{ kind: "idle" | "busy" } | { kind: "done"; text: string } | { kind: "failed"; message: string }>({ kind: "idle" });

  async function translate() {
    setState({ kind: "busy" });
    try {
      const result = await postJson<AiTranslation>(`/v1${path}`, { ...body, language }, csrfToken);
      setState({ kind: "done", text: result.text });
    } catch (error) {
      setState({ kind: "failed", message: t(aiErrorKey(error instanceof ApiError ? error.code : null)) });
    }
  }

  if (state.kind === "done") {
    return (
      <div className="mt-1 rounded-md border border-dashed border-line p-2 text-sm" data-testid="translation">
        <p className="text-xs font-semibold text-ink-muted">{t("machineTranslation")}</p>
        <p lang={language} dir={language === "ar" ? "rtl" : "ltr"} className="whitespace-pre-wrap">{state.text}</p>
        <button type="button" onClick={() => setState({ kind: "idle" })} className="text-xs font-semibold text-brand underline">{t("hideTranslation")}</button>
      </div>
    );
  }

  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <button type="button" onClick={() => void translate()} disabled={state.kind === "busy"}
        className="text-xs font-semibold text-brand underline disabled:opacity-60">
        {state.kind === "busy" ? t("translating") : t("translate")}
      </button>
      {state.kind === "failed" && <span role="alert" className="text-xs text-bad">{state.message}</span>}
    </span>
  );
}

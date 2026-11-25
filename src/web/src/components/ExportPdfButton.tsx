"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { getJson, postJson } from "@/lib/client-api";

interface Export {
  id: string;
  status: "Pending" | "Ready" | "Failed";
}

/**
 * "Download PDF" (module 11, task F1): asks for a branded PDF of an incident or a service request
 * in the page's language, waits for the worker to make it, then downloads it. Reports (task F2)
 * pass their filter as `query`; the language then goes in the query too.
 */
export function ExportPdfButton({ path, csrfToken, query }: { path: string; csrfToken: string; query?: string }) {
  const t = useTranslations("exports");
  const locale = useLocale();
  const [state, setState] = useState<"idle" | "busy" | "failed">("idle");

  async function run() {
    setState("busy");
    try {
      const language = locale === "ar" ? "ar" : "en";
      const url = query === undefined ? `/v1${path}/exports` : `/v1${path}/exports?${new URLSearchParams([...new URLSearchParams(query), ["language", language]])}`;
      let current = await postJson<Export>(url, { language }, csrfToken);
      for (let i = 0; i < 40 && current.status === "Pending"; i++) {
        await new Promise((resolve) => setTimeout(resolve, 1500));
        current = await getJson<Export>(`/v1/exports/${current.id}`);
      }
      if (current.status !== "Ready") {
        setState("failed");
        return;
      }
      // A file download through the API proxy, not a page of the app.
      const link = document.createElement("a");
      link.href = `/bff/api/v1/exports/${current.id}/file`;
      link.click();
      setState("idle");
    } catch {
      setState("failed");
    }
  }

  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <button type="button" onClick={() => void run()} disabled={state === "busy"} data-testid="export-pdf"
        className="rounded-md border border-line px-3 py-1.5 text-sm font-semibold disabled:opacity-60">
        {state === "busy" ? t("making") : t("download")}
      </button>
      <span aria-live="polite" className="text-sm text-bad">{state === "failed" ? t("failed") : ""}</span>
    </span>
  );
}

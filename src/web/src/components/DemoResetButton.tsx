"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { postJson } from "@/lib/client-api";

const buttonClass =
  "rounded-md border border-line px-3 py-2 text-sm font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60";

/**
 * "Reset demo" (task D): asks for confirmation, then restores every sample organisation
 * (POST /api/v1/demo/reset) and reloads the page. Demo administrators only.
 */
export function DemoResetButton({ csrfToken }: { csrfToken: string }) {
  const t = useTranslations("demo");
  const [status, setStatus] = useState<"idle" | "confirm" | "running" | "done" | "failed">("idle");

  const reset = async () => {
    setStatus("running");
    try {
      await postJson("/v1/demo/reset", undefined, csrfToken);
      setStatus("done");
      window.location.reload();
    } catch {
      setStatus("failed");
    }
  };

  return (
    <div className="flex flex-col gap-2">
      {status === "confirm" || status === "running" ? (
        <>
          <p className="text-sm text-ink" id="demo-reset-warning">
            {t("resetWarning")}
          </p>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              className={`${buttonClass} bg-bad text-surface`}
              aria-describedby="demo-reset-warning"
              disabled={status === "running"}
              onClick={reset}
            >
              {status === "running" ? t("resetting") : t("resetConfirm")}
            </button>
            <button type="button" className={`${buttonClass} text-ink`} disabled={status === "running"} onClick={() => setStatus("idle")}>
              {t("resetCancel")}
            </button>
          </div>
        </>
      ) : (
        <button type="button" className={`${buttonClass} text-ink`} onClick={() => setStatus("confirm")}>
          {t("reset")}
        </button>
      )}
      <p role="status" className="text-sm text-ink-muted">
        {status === "done" && t("resetDone")}
        {status === "failed" && <span className="text-bad">{t("resetFailed")}</span>}
      </p>
    </div>
  );
}

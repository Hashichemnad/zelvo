"use client";

import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import { formatNumber, type DigitSystem } from "@/lib/format";

const buttonClass =
  "rounded-md border border-line px-3 py-2 text-sm font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60";

/** How often the panel asks how far the reset has got. */
export const RESET_POLL_MS = 2000;

/** GET /api/v1/demo/reset. */
export interface DemoResetStatus {
  state: "Idle" | "Running" | "Succeeded" | "Failed";
  id: string | null;
  organisation: string | null;
  stage: "Emptying" | "Seeding" | "Indexing" | null;
  done: number;
  total: number;
  percent: number;
}

type Status = "idle" | "confirm" | "starting" | "running" | "done" | "failed" | "busy";

/**
 * "Reset demo" (task D): asks for confirmation, then starts the reset of every sample organisation
 * (POST /api/v1/demo/reset, a background job since task L1's fix) and follows it
 * (GET /api/v1/demo/reset) with a progress bar until it ends, then reloads the page. A reset
 * already running when the panel opens is followed too. Demo administrators only.
 */
export function DemoResetButton({ csrfToken, organisationNames, digits = "latn" }: {
  csrfToken: string;
  /** Each sample organisation's name in the page's language, by slug (plain data: this is a client component). */
  organisationNames: Record<string, string>;
  digits?: DigitSystem;
}) {
  const t = useTranslations("demo");
  const locale = useLocale();
  const [status, setStatus] = useState<Status>("idle");
  const [progress, setProgress] = useState<DemoResetStatus | null>(null);
  // The reset being followed: its id, or null for "whichever is running" (after a 409).
  const [watch, setWatch] = useState<{ id: string | null } | null>(null);

  // Another administrator (or this one in another tab) may have started a reset already.
  useEffect(() => {
    let cancelled = false;
    void getJson<DemoResetStatus>("/v1/demo/reset")
      .then((current) => {
        if (!cancelled && current.state === "Running") {
          setProgress(current);
          setStatus("running");
          setWatch({ id: current.id });
        }
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!watch) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const tick = async () => {
      let current: DemoResetStatus | null = null;
      try {
        current = await getJson<DemoResetStatus>("/v1/demo/reset");
      } catch {
        // The API may be busy for a moment; keep asking.
      }
      if (stopped) return;
      const ours = current !== null && (watch.id === null || current.id === watch.id);
      if (current?.state === "Succeeded" && ours) {
        setProgress(current);
        setStatus("done");
        window.location.reload();
        return;
      }
      if (current?.state === "Failed" && ours) {
        setProgress(current);
        setStatus("failed");
        return;
      }
      if (current?.state === "Running") {
        setProgress(current);
        setStatus("running");
      }
      timer = setTimeout(() => void tick(), RESET_POLL_MS);
    };
    timer = setTimeout(() => void tick(), RESET_POLL_MS);
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [watch]);

  const reset = async () => {
    setStatus("starting");
    try {
      const started = await postJson<DemoResetStatus>("/v1/demo/reset", undefined, csrfToken);
      setProgress(started);
      setStatus("running");
      setWatch({ id: started.id });
    } catch (error) {
      if (error instanceof ApiError && (error.status === 409 || error.code === "demo-reset-running")) {
        setStatus("busy");
        setWatch({ id: null });
      } else {
        setStatus("failed");
      }
    }
  };

  const running = status === "starting" || status === "running" || status === "busy";
  const stage = progress?.organisation && progress.stage
    ? t(`resetStage${progress.stage}`, { organisation: organisationNames[progress.organisation] ?? progress.organisation })
    : null;

  return (
    <div className="flex flex-col gap-2">
      {running ? (
        <div className="flex flex-col gap-1">
          <p className="text-sm text-ink" id="demo-reset-running">
            {status === "busy" ? t("resetBusy") : t("resetRunning")}
          </p>
          <progress
            className="h-2 w-full accent-brand"
            max={100}
            value={progress?.percent ?? 0}
            aria-label={t("resetProgress")}
            aria-describedby="demo-reset-running"
          />
          <p className="text-sm text-ink-muted" aria-live="polite">
            {t("resetPercent", { percent: formatNumber(progress?.percent ?? 0, locale, digits) })}
            {stage && <> · <span dir="auto">{stage}</span></>}
          </p>
        </div>
      ) : status === "confirm" ? (
        <>
          <p className="text-sm text-ink" id="demo-reset-warning">
            {t("resetWarning")}
          </p>
          <div className="flex flex-wrap gap-2">
            <button type="button" className={`${buttonClass} bg-bad text-surface`} aria-describedby="demo-reset-warning" onClick={reset}>
              {t("resetConfirm")}
            </button>
            <button type="button" className={`${buttonClass} text-ink`} onClick={() => setStatus("idle")}>
              {t("resetCancel")}
            </button>
          </div>
        </>
      ) : (
        <button type="button" className={`${buttonClass} text-ink`} disabled={status === "done"} onClick={() => setStatus("confirm")}>
          {t("reset")}
        </button>
      )}
      <p role="status" className="text-sm text-ink-muted">
        {status === "starting" && t("resetting")}
        {status === "done" && t("resetDone")}
        {status === "failed" && <span className="text-bad">{t("resetFailed")}</span>}
      </p>
    </div>
  );
}

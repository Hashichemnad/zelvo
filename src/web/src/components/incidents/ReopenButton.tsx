"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { postJson } from "@/lib/client-api";
import { buttonClass } from "./ReportIncidentForm";

/** Lets the requester reopen their resolved incident until it closes (decision 2026-11-22). */
export function ReopenButton({ incidentId, csrfToken }: { incidentId: string; csrfToken: string }) {
  const t = useTranslations("incidents.reopen");
  const router = useRouter();
  const [status, setStatus] = useState<"idle" | "saving" | "failed">("idle");

  async function reopen() {
    setStatus("saving");
    try {
      await postJson(`/v1/incidents/${incidentId}/transitions`, { state: "Reopened" }, csrfToken);
      setStatus("idle");
      router.refresh();
    } catch {
      setStatus("failed");
    }
  }

  return (
    <div className="space-y-2 rounded-lg border border-line p-4">
      <p>{t("hint")}</p>
      <button type="button" onClick={() => void reopen()} disabled={status === "saving"} className={buttonClass}>
        {t("button")}
      </button>
      <p role="status" aria-live="polite" className="text-bad">
        {status === "failed" ? t("failed") : ""}
      </p>
    </div>
  );
}

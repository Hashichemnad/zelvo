"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError, putJson } from "@/lib/client-api";
import type { AttachmentSettings } from "@/lib/incidents";

/** Settings → Attachments: largest file and accepted file types (decision 2026-11-22); whether files are scanned. */
export function AttachmentSettingsForm({ initial, csrfToken }: { initial: AttachmentSettings; csrfToken: string }) {
  const t = useTranslations("settings.attachments");
  const tCommon = useTranslations("common");
  const id = useId();
  const [maxSizeMb, setMaxSizeMb] = useState(initial.maxSizeMb);
  const [allowed, setAllowed] = useState(new Set(initial.allowedExtensions));
  const [status, setStatus] = useState<"idle" | "saving" | "saved" | "failed" | "rateLimited">("idle");

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setStatus("saving");
    try {
      const saved = await putJson<AttachmentSettings>(
        "/v1/settings/attachments",
        { maxSizeMb, allowedExtensions: initial.availableExtensions.filter((e) => allowed.has(e)) },
        csrfToken,
      );
      setAllowed(new Set(saved.allowedExtensions));
      setStatus("saved");
    } catch (error) {
      setStatus(error instanceof ApiError && error.rateLimited ? "rateLimited" : "failed");
    }
  }

  return (
    <form onSubmit={save} className="max-w-2xl space-y-5" data-testid="attachment-settings">
      <p className={initial.scanningEnabled ? "" : "font-semibold text-warn"}>{initial.scanningEnabled ? t("scanningOn") : t("scanningOff")}</p>
      <div className="space-y-1">
        <label htmlFor={`${id}-size`} className="block font-semibold">
          {t("maxSize")}
        </label>
        <input id={`${id}-size`} type="number" min={1} max={100} value={maxSizeMb} onChange={(e) => setMaxSizeMb(Number(e.target.value))}
          aria-describedby={`${id}-size-hint`} className="w-32 rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand" />
        <p id={`${id}-size-hint`} className="text-sm text-ink-muted">{t("maxSizeHint")}</p>
      </div>
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("types")}</legend>
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          {initial.availableExtensions.map((extension) => (
            <label key={extension} className="flex items-center gap-2" dir="ltr">
              <input
                type="checkbox"
                checked={allowed.has(extension)}
                onChange={(e) => {
                  const next = new Set(allowed);
                  if (e.target.checked) next.add(extension);
                  else next.delete(extension);
                  setAllowed(next);
                }}
              />
              .{extension}
            </label>
          ))}
        </div>
      </fieldset>
      <div className="flex flex-wrap items-center gap-4">
        <button type="submit" disabled={status === "saving"} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60">
          {status === "saving" ? t("saving") : t("save")}
        </button>
        <p role="status" aria-live="polite" className={status === "saved" ? "text-ok" : "text-bad"}>
          {status === "saved" ? t("saved") : status === "failed" ? t("failed") : status === "rateLimited" ? tCommon("rateLimited") : ""}
        </p>
      </div>
    </form>
  );
}

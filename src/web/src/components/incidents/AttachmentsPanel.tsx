"use client";

import { useId, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, deleteRequest, postFile } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { formatSize, type Attachment } from "@/lib/incidents";
import { secondaryButtonClass } from "./ReportIncidentForm";

export interface AttachmentsPanelProps {
  incidentId: string;
  attachments: Attachment[];
  csrfToken: string;
  /** Whether the viewer may attach files (requester or staff, incident not closed). */
  canUpload: boolean;
  limits: { maxSizeMb: number; allowedExtensions: string[] } | null;
  display: DateDisplayOptions;
}

/**
 * Files attached to an incident (decision 2026-11-22): each shows its malware scan status, and
 * only files scanned clean can be downloaded.
 */
export function AttachmentsPanel({ incidentId, attachments, csrfToken, canUpload, limits, display }: AttachmentsPanelProps) {
  const t = useTranslations("incidents.attachments");
  const tCommon = useTranslations("common");
  const router = useRouter();
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [message, setMessage] = useState<{ text: string; bad: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const base = `/v1/incidents/${incidentId}/attachments`;
  const numbering = display.digits;

  async function upload(event: React.FormEvent) {
    event.preventDefault();
    const file = input.current?.files?.[0];
    if (!file) {
      return;
    }
    setBusy(true);
    try {
      await postFile(base, file, csrfToken);
      setMessage({ text: t("uploaded"), bad: false });
      if (input.current) input.current.value = "";
      router.refresh();
    } catch (error) {
      const code = error instanceof ApiError ? error.code : null;
      setMessage({
        text: error instanceof ApiError && error.rateLimited ? tCommon("rateLimited") : code && t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.failed"),
        bad: true,
      });
    } finally {
      setBusy(false);
    }
  }

  async function remove(attachment: Attachment) {
    setBusy(true);
    try {
      await deleteRequest(`${base}/${attachment.id}`, csrfToken);
      setMessage({ text: t("removed"), bad: false });
      router.refresh();
    } catch (error) {
      const code = error instanceof ApiError ? error.code : null;
      setMessage({ text: code && t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.failed"), bad: true });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3" data-testid="attachments">
      <h2 id={`${id}-heading`} className="text-xl font-bold">
        {t("heading")}
      </h2>
      {attachments.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <ul className="divide-y divide-line rounded-lg border border-line">
          {attachments.map((attachment) => (
            <li key={attachment.id} className="flex flex-wrap items-center justify-between gap-2 p-3">
              <div className="min-w-0 space-y-0.5">
                {attachment.downloadable ? (
                  <a
                    href={`/bff/api${base}/${attachment.id}/content`}
                    className="font-semibold break-all text-brand underline-offset-4 hover:underline"
                    aria-label={t("download", { name: attachment.fileName })}
                    dir="auto"
                    download
                  >
                    {attachment.fileName}
                  </a>
                ) : (
                  <span className="font-semibold break-all" dir="auto">
                    {attachment.fileName}
                  </span>
                )}
                <p className="text-sm text-ink-muted">
                  {formatSize(attachment.size, display.locale, numbering)} ·{" "}
                  <span className={attachment.scanStatus === "Infected" ? "font-semibold text-bad" : attachment.scanStatus === "Pending" ? "text-warn" : ""}>
                    {t(`status.${attachment.scanStatus}`)}
                  </span>{" "}
                  · {t("uploadedBy", { name: attachment.uploadedBy?.displayName ?? "—", date: formatDateTime(new Date(attachment.uploadedAt), display) })}
                </p>
              </div>
              {attachment.canDelete && (
                <button type="button" className={secondaryButtonClass} disabled={busy} onClick={() => void remove(attachment)}>
                  {t("remove", { name: attachment.fileName })}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {canUpload && (
        <form onSubmit={upload} className="space-y-2">
          <label htmlFor={`${id}-file`} className="block font-semibold">
            {t("choose")}
          </label>
          <input
            id={`${id}-file`}
            ref={input}
            type="file"
            name="file"
            accept={limits?.allowedExtensions.map((e) => `.${e}`).join(",")}
            aria-describedby={limits ? `${id}-limits` : undefined}
            className="block text-sm"
          />
          {limits && (
            <p id={`${id}-limits`} className="text-sm text-ink-muted">
              {t("limits", { size: limits.maxSizeMb, types: limits.allowedExtensions.join(", ") })}
            </p>
          )}
          <button type="submit" className={secondaryButtonClass} disabled={busy}>
            {busy ? t("uploading") : t("upload")}
          </button>
        </form>
      )}
      <p role="status" aria-live="polite" className={message?.bad ? "text-bad" : "text-ok"}>
        {message?.text ?? ""}
      </p>
    </section>
  );
}

"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { SaveStatusLine, useSaver } from "@/components/directory/common";
import { buttonClass, inputClass } from "@/components/incidents/ReportIncidentForm";
import { postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { RecordComment } from "@/lib/records";

/** A record's conversation: public replies (staff and the requester) and, for staff, internal notes. */
export function RecordConversation({ recordType, recordId, initial, staff, canWrite, csrfToken, display }: {
  recordType: string;
  recordId: string;
  initial: RecordComment[];
  staff: boolean;
  canWrite: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("records.conversation");
  const locale = useLocale();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [comments, setComments] = useState(initial);
  const [body, setBody] = useState("");
  const [internal, setInternal] = useState(false);

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="record-conversation">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {comments.length === 0 ? <p>{t("none")}</p> : (
        <ol className="space-y-2">
          {comments.map((c) => (
            <li key={c.id} className={`rounded-md p-2 ${c.visibility === "Internal" ? "border-s-4 border-warn bg-surface-muted" : "bg-surface-muted"}`}>
              <p className="text-sm text-ink-muted">
                {t("by", { name: c.author.displayName ?? "—", date: formatDateTime(new Date(c.createdAt), display) })}
                {c.visibility === "Internal" && <> · <span className="font-semibold">{t("internal")}</span></>}
              </p>
              <p className="whitespace-pre-wrap" lang={c.language} dir="auto">{c.body}</p>
            </li>
          ))}
        </ol>
      )}
      {canWrite && (
        <form className="space-y-2" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            const comment = await postJson<RecordComment>(`/v1/records/${recordType}/${recordId}/comments`,
              { body, visibility: internal ? "Internal" : "Public", language: locale }, csrfToken);
            setComments((current) => [...current, comment]);
            setBody("");
          });
        }}>
          <label htmlFor={`${id}-body`} className="block font-semibold">{internal ? t("note") : t("reply")}</label>
          <textarea id={`${id}-body`} value={body} onChange={(e) => setBody(e.target.value)} rows={4} maxLength={20000} dir="auto" className={inputClass} />
          {staff && (
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={internal} onChange={(e) => setInternal(e.target.checked)} className="size-5 accent-brand" />
              {t("internalNote")}
            </label>
          )}
          <button type="submit" className={buttonClass} disabled={busy || !body.trim()}>{t("send")}</button>
        </form>
      )}
      <SaveStatusLine status={status} />
    </section>
  );
}

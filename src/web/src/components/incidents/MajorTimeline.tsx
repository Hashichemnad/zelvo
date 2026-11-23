"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale, type MajorIncidentUpdate } from "@/lib/incidents";
import { inputClass, secondaryButtonClass } from "./ReportIncidentForm";
import { Localized } from "./StateBadge";

export interface MajorTimelineProps {
  incidentId: string;
  updates: MajorIncidentUpdate[];
  /** Holders of incidents.major on a major incident may post. */
  canPost: boolean;
  /** Staff see who can read each update. */
  staff: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}

/** The communication timeline of a major incident: bilingual updates, for staff or also requesters. */
export function MajorTimeline({ incidentId, updates, canPost, staff, csrfToken, display }: MajorTimelineProps) {
  const t = useTranslations("incidents.timeline");
  const tCommon = useTranslations("common");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [en, setEn] = useState("");
  const [ar, setAr] = useState("");
  const [audience, setAudience] = useState<"Internal" | "Requesters">("Requesters");
  const [status, setStatus] = useState<"idle" | "posting" | "posted" | "failed" | "empty" | "rateLimited">("idle");

  async function post(event: React.FormEvent) {
    event.preventDefault();
    if (!en.trim() && !ar.trim()) {
      setStatus("empty");
      return;
    }
    setStatus("posting");
    try {
      await postJson(`/v1/incidents/${incidentId}/updates`, { en: en.trim() || null, ar: ar.trim() || null, audience }, csrfToken);
      setEn("");
      setAr("");
      setStatus("posted");
      router.refresh();
    } catch (error) {
      setStatus(error instanceof ApiError && error.rateLimited ? "rateLimited" : "failed");
    }
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3" data-testid="major-timeline">
      <h2 id={`${id}-heading`} className="text-xl font-bold">
        {t("heading")}
      </h2>
      {updates.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <ol className="space-y-3">
          {updates.map((update) => (
            <li key={update.id} className="rounded-lg border border-line p-3">
              <p className="whitespace-pre-wrap">
                <Localized {...inLanguage(update.body, locale)} />
              </p>
              <p className="text-sm text-ink-muted">
                {t("postedBy", { name: update.postedBy.displayName ?? "—", date: formatDateTime(new Date(update.postedAt), display) })}
                {staff && <> · {t(`audiences.${update.audience}`)}</>}
                {update.ticketId !== incidentId && (
                  <>
                    {" "}
                    · <span dir="ltr">{t("from", { number: update.ticketNumber })}</span>
                  </>
                )}
              </p>
            </li>
          ))}
        </ol>
      )}
      {canPost && (
        <form onSubmit={post} className="space-y-3 rounded-lg border border-line p-4">
          <div className="space-y-1">
            <label htmlFor={`${id}-en`} className="block font-semibold">
              {t("english")}
            </label>
            <textarea id={`${id}-en`} value={en} onChange={(e) => setEn(e.target.value)} rows={3} lang="en" dir="ltr" maxLength={4000} className={inputClass} />
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-ar`} className="block font-semibold">
              {t("arabic")}
            </label>
            <textarea id={`${id}-ar`} value={ar} onChange={(e) => setAr(e.target.value)} rows={3} lang="ar" dir="rtl" maxLength={4000} className={inputClass} />
          </div>
          <fieldset className="space-y-1">
            <legend className="font-semibold">{t("audience")}</legend>
            {(["Requesters", "Internal"] as const).map((value) => (
              <label key={value} className="flex items-center gap-2">
                <input type="radio" name={`${id}-audience`} value={value} checked={audience === value} onChange={() => setAudience(value)} />
                {t(`audiences.${value}`)}
              </label>
            ))}
          </fieldset>
          <div className="flex flex-wrap items-center gap-4">
            <button type="submit" className={secondaryButtonClass} disabled={status === "posting"}>
              {status === "posting" ? t("posting") : t("post")}
            </button>
            <p role="status" aria-live="polite" className={status === "posted" ? "text-ok" : "text-bad"}>
              {status === "posted" ? t("posted") : status === "failed" ? t("failed") : status === "empty" ? t("empty") : status === "rateLimited" ? tCommon("rateLimited") : ""}
            </p>
          </div>
        </form>
      )}
    </section>
  );
}

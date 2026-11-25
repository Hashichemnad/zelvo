"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldInput, primaryAction } from "@/components/forms/ui";
import { ApiError, postJson } from "@/lib/client-api";
import type { DateDisplayOptions } from "@/lib/format";
import type { Rating, TrackedVoice, VoiceSettings } from "@/lib/voice";
import { HappinessMeter } from "./HappinessMeter";
import { ConfirmResolution, VoiceConversation, VoiceFacts, VoiceProgress } from "./VoiceViews";

/**
 * Following an anonymous complaint, inquiry or suggestion with its number and tracking code
 * (task C6): status, public replies, a reply, the complaint's confirmation and the happiness meter.
 * The code stays in this page's memory only.
 */
export function TrackVoice({ settings, display }: { settings: VoiceSettings | null; display: Omit<DateDisplayOptions, "locale"> }) {
  const t = useTranslations("voice.track");
  const locale = useLocale();
  const id = useId();
  const [number, setNumber] = useState("");
  const [code, setCode] = useState("");
  const [tracked, setTracked] = useState<TrackedVoice | null>(null);
  const [status, setStatus] = useState<"idle" | "loading" | "notFound" | "failed">("idle");
  const credentials = () => ({ number: number.trim(), code: code.trim() });
  const refresh = async () => setTracked(await postJson<TrackedVoice>("/v1/public/customer-voice/track", credentials(), ""));

  async function look(event: React.FormEvent) {
    event.preventDefault();
    setStatus("loading");
    try {
      await refresh();
      setStatus("idle");
    } catch (error) {
      setTracked(null);
      setStatus(error instanceof ApiError && error.status === 404 ? "notFound" : "failed");
    }
  }

  const options = { locale, ...display };
  const record = tracked?.record;
  return (
    <div className="space-y-8">
      <form onSubmit={look} className="grid max-w-2xl gap-4 sm:grid-cols-2" data-testid="track-form">
        <div className="space-y-1">
          <label htmlFor={`${id}-number`} className="block font-semibold">{t("number")}</label>
          <input id={`${id}-number`} value={number} onChange={(e) => setNumber(e.target.value)} required dir="ltr" autoComplete="off" className={fieldInput} placeholder="CMP-000123" />
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-code`} className="block font-semibold">{t("code")}</label>
          <input id={`${id}-code`} value={code} onChange={(e) => setCode(e.target.value)} required dir="ltr" autoComplete="off" className={fieldInput} />
        </div>
        <div className="sm:col-span-2">
          <button type="submit" className={primaryAction} disabled={status === "loading"}>{t("show")}</button>
          <p role="status" aria-live="polite" className="mt-2 text-bad">{status === "notFound" ? t("notFound") : status === "failed" ? t("failed") : ""}</p>
        </div>
      </form>
      {tracked && record && (
        <article aria-labelledby={`${id}-record`} className="space-y-8" data-testid="tracked-record">
          <header className="space-y-1">
            <p className="font-mono text-ink-muted" dir="ltr">{record.number}</p>
            <h2 id={`${id}-record`} className="text-2xl font-bold text-brand" dir="auto">{record.title}</h2>
          </header>
          <VoiceProgress record={record} />
          <VoiceFacts record={record} display={options} settings={settings} incidentSettings={null} staff={false} />
          {record.canConfirm && (
            <ConfirmResolution send={async (accept, comment) => {
              setTracked(await postJson<TrackedVoice>("/v1/public/customer-voice/track/confirmation", { ...credentials(), accept, comment: comment ?? undefined, language: locale }, ""));
            }} />
          )}
          {settings?.happiness.enabled && settings.happiness.askOnClosure && (record.canRate || record.rating) && (
            <HappinessMeter settings={settings.happiness} kind="ticket" rating={record.rating}
              send={(score, comment) => postJson<Rating>("/v1/public/customer-voice/track/happiness", { ...credentials(), score, comment: comment || undefined }, "")} />
          )}
          <VoiceConversation comments={tracked.comments} display={options} staff={false} closed={record.state === "Closed"}
            send={async (body) => {
              await postJson("/v1/public/customer-voice/track/comments", { ...credentials(), body, language: locale }, "");
              await refresh();
            }} />
        </article>
      )}
    </div>
  );
}

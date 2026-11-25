"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { faceOf, type HappinessSettings, type Rating } from "@/lib/voice";

const deviceKey = "zelvo.happinessDevice";

/** A random id for this browser, kept on the device, so a visitor rates the portal once a day (only its hash is stored). */
function deviceId(): string {
  try {
    const existing = window.localStorage.getItem(deviceKey);
    if (existing) return existing;
    const created = crypto.randomUUID();
    window.localStorage.setItem(deviceKey, created);
    return created;
  } catch {
    return crypto.randomUUID();
  }
}

export interface HappinessMeterProps {
  settings: HappinessSettings;
  /** "ticket": a resolved ticket's question; "portal": the general rating. */
  kind: "ticket" | "portal";
  /** Already given. */
  rating?: Rating | null;
  /** Sends the rating; throws ApiError when refused. */
  send: (score: number, comment: string, device: string | null) => Promise<Rating>;
}

/**
 * The happiness meter (GOV-2): the tenant's question and one labelled button per point (faces are
 * decorative), then an optional comment. A radio group, so it works with the keyboard and screen
 * readers in both languages.
 */
export function HappinessMeter({ settings, kind, rating: initial, send }: HappinessMeterProps) {
  const t = useTranslations("voice.happiness");
  const locale = useLocale() as Locale;
  const id = useId();
  const [rating, setRating] = useState<Rating | null>(initial ?? null);
  const [score, setScore] = useState<number | null>(null);
  const [comment, setComment] = useState("");
  const [status, setStatus] = useState<"idle" | "saving" | "failed" | "already">("idle");
  const question = textOf(kind === "ticket" ? settings.question : settings.portalQuestion, locale);

  if (rating) {
    return (
      <section aria-labelledby={`${id}-done`} className="rounded-lg border border-line bg-surface-muted p-4" data-testid="happiness-done">
        <h2 id={`${id}-done`} className="font-semibold">{t("thanks")}</h2>
        <p>
          <span aria-hidden="true" className="me-2">{faceOf(rating.score, rating.points)}</span>
          {t("yourRating", { label: textOf(settings.labels[rating.score - 1], locale) || String(rating.score) })}
        </p>
      </section>
    );
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (score === null) return;
    setStatus("saving");
    try {
      setRating(await send(score, comment.trim(), kind === "portal" ? deviceId() : null));
      setStatus("idle");
    } catch (error) {
      setStatus(error instanceof ApiError && error.code === "already-rated" ? "already" : "failed");
    }
  }

  return (
    <form onSubmit={submit} className="space-y-3 rounded-lg border border-line p-4" data-testid={`happiness-${kind}`}>
      <fieldset className="space-y-3">
        <legend className="font-semibold">{question}</legend>
        <div className="flex flex-wrap gap-2">
          {settings.labels.map((label, index) => {
            const value = index + 1;
            const name = textOf(label, locale) || String(value);
            return (
              <label key={value}
                className={`flex min-h-11 min-w-24 cursor-pointer flex-col items-center gap-1 rounded-lg border px-3 py-2 text-sm font-semibold focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-brand ${score === value ? "border-brand bg-surface-muted text-brand" : "border-line"}`}>
                <input type="radio" name={`${id}-score`} value={value} checked={score === value} onChange={() => setScore(value)} className="sr-only" />
                <span aria-hidden="true" className="text-2xl">{faceOf(value, settings.points)}</span>
                <span>{name}</span>
              </label>
            );
          })}
        </div>
      </fieldset>
      <div className="space-y-1">
        <label htmlFor={`${id}-comment`} className="block text-sm">{t("comment")}</label>
        <textarea id={`${id}-comment`} value={comment} onChange={(e) => setComment(e.target.value)} rows={2} maxLength={2000} dir="auto"
          className="w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand" />
      </div>
      <button type="submit" disabled={score === null || status === "saving"}
        className="rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60">
        {t("send")}
      </button>
      <p role="status" aria-live="polite" className={status === "failed" ? "text-bad" : ""}>
        {status === "saving" ? t("saving") : status === "failed" ? t("failed") : status === "already" ? t("already") : ""}
      </p>
    </form>
  );
}

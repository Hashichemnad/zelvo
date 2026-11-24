"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale, type Person } from "@/lib/incidents";
import type { CannedResponse, Comment, CommentVisibility, RenderedResponse } from "@/lib/workspace";
import { inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";

/**
 * The conversation of an incident (module 5): public replies, which the requester sees, and
 * internal notes for staff, oldest first. Each message keeps the language it was written in and is
 * marked with it, so the agent and the requester can each work in their own language (LANG-7).
 * Task E2: messages that came by email or from a live chat say so, with the files of an email, and
 * staff see whether the email of a reply to an email ticket went out.
 */
export function ConversationList({ comments, display, staff }: { comments: Comment[]; display: DateDisplayOptions; staff: boolean }) {
  const t = useTranslations("workspace.conversation");
  const tLanguages = useTranslations("display.languages");
  const locale = useLocale();
  if (comments.length === 0) return <p className="text-ink-muted">{t("empty")}</p>;
  return (
    <ol className="space-y-3" aria-label={t("label")}>
      {comments.map((comment) => {
        const internal = comment.visibility === "Internal";
        return (
          <li
            key={comment.id}
            className={`space-y-1 rounded-lg border p-3 ${internal ? "border-warn bg-surface-muted" : comment.byStaff ? "border-line bg-surface" : "border-brand bg-surface"}`}
            data-testid="comment"
          >
            <p className="flex flex-wrap items-center gap-2 text-sm">
              <span className="font-semibold">{comment.author.displayName ?? t("someone")}</span>
              {staff && (
                <span className="rounded-full border border-line px-2 py-0.5 text-xs font-semibold">
                  {internal ? t("internalNote") : comment.byStaff ? t("publicReply") : t("fromRequester")}
                </span>
              )}
              <span className="text-ink-muted">{formatDateTime(new Date(comment.createdAt), display)}</span>
              <span className="text-xs text-ink-muted">{t("writtenIn", { language: tLanguages(comment.language) })}</span>
              {comment.source && comment.source !== "Web" && <span className="text-xs text-ink-muted">{t(`sources.${comment.source}`)}</span>}
              {staff && comment.emailDelivery && (
                <span className={`text-xs ${comment.emailDelivery === "Failed" ? "font-semibold text-bad" : "text-ink-muted"}`}>{t(`delivery.${comment.emailDelivery}`)}</span>
              )}
            </p>
            <p lang={comment.language} dir="auto" className="whitespace-pre-wrap">
              {comment.body}
            </p>
            {comment.attachments && comment.attachments.length > 0 && (
              <p className="text-xs text-ink-muted">
                {t("files", { count: comment.attachments.length })}: <span dir="auto">{comment.attachments.map((a) => a.fileName).join(locale === "ar" ? "، " : ", ")}</span>
              </p>
            )}
            {comment.mentions.length > 0 && (
              <p className="text-xs text-ink-muted">{t("mentioned", { names: comment.mentions.map((m) => m.displayName ?? "").join(locale === "ar" ? "، " : ", ") })}</p>
            )}
          </li>
        );
      })}
    </ol>
  );
}

/** Event the workspace's keyboard shortcuts send to start a reply or a note (detail: the visibility). */
export const composeEvent = "zelvo:compose";

export interface ComposerProps {
  incidentId: string;
  csrfToken: string;
  /** Whether internal notes, mentions and canned responses are offered (staff). */
  staff: boolean;
  /** The requester's own language, shown so the agent knows who reads the reply. */
  requesterLanguage?: "en" | "ar" | null;
  responses: CannedResponse[];
  /** Staff who can be @mentioned. */
  people: Person[];
  disabled?: boolean;
}

/**
 * Writes a public reply or an internal note. Staff pick a canned response in either language (it
 * is filled in for the ticket by the API), @mention colleagues and choose the language they write
 * in. One-click translation comes with the AI features (task E5).
 */
export function Composer({ incidentId, csrfToken, staff, requesterLanguage, responses, people, disabled }: ComposerProps) {
  const t = useTranslations("workspace.composer");
  const tLanguages = useTranslations("display.languages");
  const tCommon = useTranslations("common");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const textRef = useRef<HTMLTextAreaElement>(null);
  const [visibility, setVisibility] = useState<CommentVisibility>("Public");
  const [language, setLanguage] = useState<"en" | "ar">(locale);
  const [body, setBody] = useState("");
  const [cannedResponseId, setCannedResponseId] = useState<string | null>(null);
  const [mentions, setMentions] = useState<Person[]>([]);
  const [status, setStatus] = useState<{ kind: "idle" | "sending" | "sent" } | { kind: "failed"; message: string }>({ kind: "idle" });

  useEffect(() => {
    const start = (event: Event) => {
      const next = (event as CustomEvent<CommentVisibility>).detail;
      setVisibility(staff && next === "Internal" ? "Internal" : "Public");
      textRef.current?.focus();
    };
    window.addEventListener(composeEvent, start);
    return () => window.removeEventListener(composeEvent, start);
  }, [staff]);

  function failure(error: unknown): string {
    if (error instanceof ApiError) {
      if (error.rateLimited) return tCommon("rateLimited");
      if (error.code && t.has(`errors.${error.code}`)) return t(`errors.${error.code}`);
    }
    return t("failed");
  }

  async function applyResponse(responseId: string) {
    if (!responseId) return;
    try {
      const rendered = await postJson<RenderedResponse>(`/v1/incidents/${incidentId}/canned-responses/${responseId}/render`, { language }, csrfToken);
      setBody((current) => (current.trim() ? `${current.trimEnd()}\n\n${rendered.text}` : rendered.text));
      setCannedResponseId(responseId);
      if (rendered.fallback) setStatus({ kind: "failed", message: t("responseFallback") });
      textRef.current?.focus();
    } catch (error) {
      setStatus({ kind: "failed", message: failure(error) });
    }
  }

  function mention(personId: string) {
    const person = people.find((p) => p.id === personId);
    if (!person || mentions.some((m) => m.id === person.id)) return;
    setMentions((current) => [...current, person]);
    setBody((current) => `${current}${current && !current.endsWith(" ") ? " " : ""}@${person.displayName ?? ""} `);
    textRef.current?.focus();
  }

  async function send(event: React.FormEvent) {
    event.preventDefault();
    if (!body.trim()) {
      setStatus({ kind: "failed", message: t("empty") });
      return;
    }
    setStatus({ kind: "sending" });
    try {
      await postJson(`/v1/incidents/${incidentId}/comments`, {
        body,
        visibility,
        language,
        ...(staff ? { mentionIds: mentions.map((m) => m.id), cannedResponseId } : {}),
      }, csrfToken);
      setBody("");
      setMentions([]);
      setCannedResponseId(null);
      setStatus({ kind: "sent" });
      router.refresh();
    } catch (error) {
      setStatus({ kind: "failed", message: failure(error) });
    }
  }

  const internal = visibility === "Internal";
  return (
    <form onSubmit={send} className={`space-y-3 rounded-lg border p-3 ${internal ? "border-warn bg-surface-muted" : "border-line"}`} aria-labelledby={`${id}-heading`}>
      <h3 id={`${id}-heading`} className="font-semibold">{internal ? t("noteHeading") : t("replyHeading")}</h3>
      {staff && (
        <fieldset className="flex flex-wrap gap-4 text-sm">
          <legend className="sr-only">{t("visibility")}</legend>
          {(["Public", "Internal"] as const).map((value) => (
            <label key={value} className="flex items-center gap-2">
              <input type="radio" name={`${id}-visibility`} value={value} checked={visibility === value} onChange={() => setVisibility(value)} />
              {value === "Public" ? t("public") : t("internal")}
            </label>
          ))}
        </fieldset>
      )}
      {!internal && requesterLanguage && (
        <p className="text-sm text-ink-muted">{t("requesterLanguage", { language: tLanguages(requesterLanguage) })}</p>
      )}
      <div className="flex flex-wrap gap-3">
        <label className="space-y-1 text-sm">
          <span className="block font-semibold">{t("language")}</span>
          <select value={language} onChange={(e) => setLanguage(e.target.value as "en" | "ar")} className={inputClass}>
            <option value="en">{tLanguages("en")}</option>
            <option value="ar">{tLanguages("ar")}</option>
          </select>
        </label>
        {staff && responses.length > 0 && (
          <label className="space-y-1 text-sm">
            <span className="block font-semibold">{t("cannedResponse")}</span>
            <select value="" onChange={(e) => void applyResponse(e.target.value)} className={inputClass}>
              <option value="">{t("chooseResponse")}</option>
              {responses.map((r) => {
                const title = inLanguage(r.title, locale);
                return <option key={r.id} value={r.id} lang={title.lang}>{title.value}</option>;
              })}
            </select>
          </label>
        )}
        {staff && people.length > 0 && (
          <label className="space-y-1 text-sm">
            <span className="block font-semibold">{t("mention")}</span>
            <select value="" onChange={(e) => mention(e.target.value)} className={inputClass}>
              <option value="">{t("choosePerson")}</option>
              {people.map((p) => <option key={p.id} value={p.id}>{p.displayName ?? p.id}</option>)}
            </select>
          </label>
        )}
      </div>
      <label htmlFor={`${id}-body`} className="sr-only">{internal ? t("noteHeading") : t("replyHeading")}</label>
      <textarea
        id={`${id}-body`}
        ref={textRef}
        value={body}
        onChange={(e) => setBody(e.target.value)}
        rows={5}
        maxLength={20000}
        lang={language}
        dir="auto"
        disabled={disabled}
        className={inputClass}
        data-testid="composer-body"
      />
      {mentions.length > 0 && (
        <ul className="flex flex-wrap gap-2 text-sm" aria-label={t("mentions")}>
          {mentions.map((m) => (
            <li key={m.id} className="flex items-center gap-1 rounded-full border border-line px-2 py-0.5">
              @{m.displayName}
              <button type="button" onClick={() => setMentions((current) => current.filter((x) => x.id !== m.id))}
                aria-label={t("removeMention", { name: m.displayName ?? "" })} className="font-semibold text-bad">×</button>
            </li>
          ))}
        </ul>
      )}
      <div className="flex flex-wrap items-center gap-3">
        <button type="submit" disabled={disabled || status.kind === "sending"} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface disabled:opacity-60">
          {internal ? t("addNote") : t("sendReply")}
        </button>
        {staff && (
          <button type="button" disabled className={`${secondaryButtonClass} opacity-60`} title={t("translateLater")}>
            {t("translate")}
          </button>
        )}
        <p aria-live="polite" className={status.kind === "failed" ? "text-bad" : "text-ink-muted"}>
          {status.kind === "sending" ? t("sending") : status.kind === "sent" ? t("sent") : status.kind === "failed" ? status.message : ""}
        </p>
      </div>
      {staff && <p className="text-xs text-ink-muted">{t("translateLater")}</p>}
    </form>
  );
}

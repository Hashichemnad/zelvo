"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import { PrivacyNoticeCheckbox, usePrivacyNoticePublished } from "@/components/privacy/PrivacyNoticeCheckbox";
import { FormRenderer } from "@/components/forms/FormRenderer";
import { fieldInput, primaryAction } from "@/components/forms/ui";
import { checkAnswers } from "@/lib/forms/check";
import type { AnswerError, Answers, ResolvedForm } from "@/lib/forms/types";
import { selectableNodes, textOf, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import type { AnonymousSubmission, VoiceRecord, VoiceType } from "@/lib/voice";

interface SearchHit {
  type: string;
  id: string;
  title: { en: string | null; ar: string | null };
  url: string | null;
}

/**
 * Inquiries' fast answer (module 3): knowledge articles that match what the person is typing,
 * before they send the inquiry.
 */
export function KnowledgeSuggestions({ text }: { text: string }) {
  const t = useTranslations("voice.form");
  const locale = useLocale() as Locale;
  const [hits, setHits] = useState<SearchHit[]>([]);
  useEffect(() => {
    const q = text.trim();
    const timer = setTimeout(() => {
      if (q.length < 3) {
        setHits([]);
        return;
      }
      getJson<{ items: SearchHit[] }>(`/v1/search?${new URLSearchParams({ q, types: "article", pageSize: "5" })}`)
        .then((page) => setHits(page.items))
        .catch(() => setHits([]));
    }, 400);
    return () => clearTimeout(timer);
  }, [text]);
  if (hits.length === 0) return null;
  return (
    <aside aria-labelledby="voice-suggestions" aria-live="polite" className="space-y-2 rounded-lg border border-brand p-4" data-testid="knowledge-suggestions">
      <h2 id="voice-suggestions" className="font-semibold">{t("suggestions")}</h2>
      <ul className="list-disc space-y-1 ps-5">
        {hits.map((h) => (
          <li key={h.id}><Link href={`/help/articles/${h.id}`} className="text-brand underline" dir="auto">{textOf(h.title, locale)}</Link></li>
        ))}
      </ul>
    </aside>
  );
}

export interface VoiceFormProps {
  type: VoiceType;
  /** Signed in: the session's CSRF token; visitors send anonymously (when allowed) without one. */
  csrfToken: string | null;
  categories: TreeNode[];
  locations: TreeNode[];
}

/**
 * Sending a complaint, inquiry or suggestion. People who are signed in fill the record type's form
 * for the category (C1); visitors send title and description anonymously and get the number and a
 * tracking code, shown once.
 */
export function VoiceForm({ type, csrfToken, categories, locations }: VoiceFormProps) {
  const t = useTranslations("voice.form");
  const tVoice = useTranslations("voice");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const anonymous = csrfToken === null;
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [status, setStatus] = useState<"idle" | "saving" | "invalid" | "failed" | "rateLimited" | "formInvalid" | "formChanged" | "disabled">("idle");
  const [form, setForm] = useState<ResolvedForm | null>(null);
  const [formAnswers, setFormAnswers] = useState<Answers>({});
  const [formErrors, setFormErrors] = useState<AnswerError[]>([]);
  const [reload, setReload] = useState(0);
  const [sent, setSent] = useState<AnonymousSubmission | null>(null);
  // Task F5: anonymous senders accept the privacy notice when the organisation published one.
  const noticePublished = usePrivacyNoticePublished(anonymous);
  const [noticeAccepted, setNoticeAccepted] = useState(false);

  useEffect(() => {
    if (anonymous) return;
    let cancelled = false;
    const query = new URLSearchParams({ recordType: type, language: locale });
    if (categoryId) query.set("categoryId", categoryId);
    getJson<ResolvedForm>(`/v1/forms/resolve?${query}`)
      .then((resolved) => {
        if (cancelled) return;
        setForm(resolved.form ? resolved : null);
        setFormErrors([]);
        setFormAnswers((current) => Object.fromEntries(Object.entries(current).filter(([key]) => resolved.fields.some((f) => f.field.key === key))));
      })
      .catch(() => !cancelled && setForm(null));
    return () => {
      cancelled = true;
    };
  }, [anonymous, type, categoryId, locale, reload]);

  if (sent) {
    return (
      <section aria-labelledby={`${id}-sent`} className="max-w-2xl space-y-3 rounded-lg border-2 border-ok p-4" data-testid="anonymous-sent" role="status">
        <h2 id={`${id}-sent`} className="text-xl font-semibold">{t("sentHeading")}</h2>
        <p>{t("sentIntro")}</p>
        <dl className="grid gap-2 sm:grid-cols-2">
          <div><dt className="font-semibold">{t("number")}</dt><dd className="font-mono text-xl" dir="ltr" data-testid="anonymous-number">{sent.number}</dd></div>
          <div><dt className="font-semibold">{t("code")}</dt><dd className="font-mono text-xl tracking-widest" dir="ltr" data-testid="anonymous-code">{sent.trackingCode.match(/.{1,4}/g)?.join("-")}</dd></div>
        </dl>
        <p className="font-semibold">{t("keepCode")}</p>
        <Link href="/track" className="font-semibold text-brand underline">{t("trackLink")}</Link>
      </section>
    );
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (title.trim().length < 3 || (anonymous && noticePublished && !noticeAccepted)) {
      setStatus("invalid");
      return;
    }
    let formBody: { formVersionId: string; formAnswers: Answers } | null = null;
    if (form?.form) {
      const checked = checkAnswers(form.fields.map((f) => f.field), formAnswers, form.defaults);
      setFormErrors(checked.errors);
      if (checked.errors.length > 0) {
        setStatus("formInvalid");
        return;
      }
      formBody = { formVersionId: form.form.versionId, formAnswers: checked.toSend };
    }
    setStatus("saving");
    const body: Record<string, unknown> = { type, title: title.trim(), description: description.trim() || undefined };
    try {
      if (anonymous) {
        if (noticePublished) body.privacyNoticeAccepted = true;
        setSent(await postJson<AnonymousSubmission>("/v1/public/customer-voice", body, ""));
        return;
      }
      if (categoryId) body.categoryId = categoryId;
      const created = await postJson<VoiceRecord>("/v1/customer-voice", { ...body, ...formBody }, csrfToken!);
      router.push(`/my-voice/${created.id}`);
    } catch (error) {
      const answerErrors = error instanceof ApiError ? (error.details.formErrors as AnswerError[] | undefined) : undefined;
      if (answerErrors?.length) {
        setFormErrors(answerErrors);
        setStatus("formInvalid");
        return;
      }
      if (error instanceof ApiError && error.code === "form-changed") {
        setStatus("formChanged");
        setReload((n) => n + 1);
        return;
      }
      setStatus(error instanceof ApiError && error.code === "anonymous-disabled" ? "disabled"
        : error instanceof ApiError && error.rateLimited ? "rateLimited"
        : error instanceof ApiError && error.status === 400 ? "invalid" : "failed");
    }
  }

  const choices = selectableNodes(categories);
  return (
    <form onSubmit={submit} className="max-w-2xl space-y-5" data-testid="voice-form" noValidate>
      {anonymous && <p className="rounded-md border-s-4 border-brand bg-surface-muted px-4 py-3 text-sm">{t("anonymousNotice")}</p>}
      <div className="space-y-1">
        <label htmlFor={`${id}-title`} className="block font-semibold">{t(`title.${type}`)}</label>
        <input id={`${id}-title`} value={title} onChange={(e) => { setTitle(e.target.value); setStatus("idle"); }} required minLength={3} maxLength={300} dir="auto"
          aria-invalid={status === "invalid" ? true : undefined} aria-describedby={`${id}-title-hint`} className={fieldInput} />
        <p id={`${id}-title-hint`} className="text-sm text-ink-muted">{t("titleHint")}</p>
      </div>
      {type === "Inquiry" && <KnowledgeSuggestions text={title} />}
      <div className="space-y-1">
        <label htmlFor={`${id}-description`} className="block font-semibold">{t(`description.${type}`)}</label>
        <textarea id={`${id}-description`} value={description} onChange={(e) => setDescription(e.target.value)} rows={6} maxLength={20000} dir="auto" className={fieldInput} />
      </div>
      {!anonymous && choices.length > 0 && (
        <div className="space-y-1">
          <label htmlFor={`${id}-category`} className="block font-semibold">{t("category")}</label>
          <select id={`${id}-category`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={fieldInput}>
            <option value="">{t("notSure")}</option>
            {choices.map((node) => <option key={node.id} value={node.id}>{textOf(node.path, locale) || textOf(node.name, locale)}</option>)}
          </select>
        </div>
      )}
      {form?.form && (
        <section aria-labelledby={`${id}-form`} className="space-y-3 rounded-lg border border-line p-4">
          <h2 id={`${id}-form`} className="text-lg font-semibold">{textOf(form.form.name, locale)}</h2>
          <FormRenderer fields={form.fields} answers={formAnswers} onChange={(answers) => { setFormAnswers(answers); if (status === "formInvalid") setStatus("idle"); }}
            defaults={form.defaults} references={form.references} errors={formErrors} contentLocale={locale}
            locations={locations} csrfToken={csrfToken!} idPrefix={`${id}-answers`} />
        </section>
      )}
      {anonymous && noticePublished && <PrivacyNoticeCheckbox locale={locale} checked={noticeAccepted} onChange={(c) => { setNoticeAccepted(c); setStatus("idle"); }} />}
      <button type="submit" className={primaryAction} disabled={status === "saving"}>{t(anonymous ? "sendAnonymously" : "send")}</button>
      <p role="status" aria-live="polite" className={status === "idle" || status === "saving" ? "" : "text-bad"}>
        {status === "idle" ? "" : status === "saving" ? tVoice("saving") : t(`status.${status}`)}
      </p>
    </form>
  );
}

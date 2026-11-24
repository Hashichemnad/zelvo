"use client";

import { useEffect, useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { inLanguage, type Locale } from "@/lib/incidents";
import { getJson, putJson, postJson } from "@/lib/client-api";
import { formatLongDate, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { visitorId, type MyFeedback, type PublishedArticle } from "@/lib/knowledge";
import { MarkdownView } from "./MarkdownView";

/**
 * A published article to read (portal and staff): the reader's language, or the other one with a
 * note when it is missing (product owner decision 2026-11-24); counts the view and asks
 * "Was this helpful?".
 */
export function PublishedArticleView({ article, display, csrfToken }: { article: PublishedArticle; display: DateDisplayOptions; csrfToken: string | null }) {
  const t = useTranslations("knowledge.reader");
  const locale = display.locale as Locale;
  const title = inLanguage(article.article.title, locale);
  const summary = inLanguage(article.article.summary, locale);
  const body = inLanguage(article.body, locale);
  const [views, setViews] = useState(article.article.viewCount);

  useEffect(() => {
    const visitor = csrfToken ? null : visitorId();
    if (!csrfToken && !visitor) {
      return;
    }
    postJson<{ viewCount: number }>(`/v1/knowledge/published/${article.article.id}/views`, { visitorId: visitor }, csrfToken ?? "")
      .then((result) => setViews(result.viewCount))
      .catch(() => undefined);
  }, [article.article.id, csrfToken]);

  const published = article.article.publishedAt ? formatLongDate(new Date(article.article.publishedAt), display) : null;
  return (
    <article className="space-y-6" aria-labelledby="article-title" data-testid="published-article">
      <header className="space-y-2">
        <p className="text-sm text-ink-muted">
          <span dir="ltr">{article.article.number}</span>
          {published && <> · {t("updated", { date: published })}</>} · {t("views", { count: views, formatted: formatNumber(views, display.locale, display.digits) })}
        </p>
        <h1 id="article-title" className="text-3xl font-bold" lang={title.lang} dir={title.dir}>
          {title.value}
        </h1>
        {summary.value && (
          <p className="text-lg text-ink-muted" lang={summary.lang} dir={summary.dir}>
            {summary.value}
          </p>
        )}
      </header>
      {body.lang && (
        <p role="note" className="rounded-md border border-line bg-surface-muted p-3 text-sm" data-testid="other-language-note">
          {t("otherLanguage")}
        </p>
      )}
      <MarkdownView markdown={body.value} articleId={article.article.id} lang={body.lang} dir={body.dir} />
      <FeedbackBox articleId={article.article.id} csrfToken={csrfToken} />
    </article>
  );
}

function FeedbackBox({ articleId, csrfToken }: { articleId: string; csrfToken: string | null }) {
  const t = useTranslations("knowledge.feedback");
  const id = useId();
  const [mine, setMine] = useState<MyFeedback | null>(null);
  const [helpful, setHelpful] = useState<boolean | null>(null);
  const [comment, setComment] = useState("");
  const [status, setStatus] = useState<"idle" | "saved" | "failed">("idle");

  useEffect(() => {
    const visitor = csrfToken ? null : visitorId();
    const query = visitor ? `?visitorId=${encodeURIComponent(visitor)}` : "";
    getJson<MyFeedback>(`/v1/knowledge/published/${articleId}/feedback${query}`)
      .then((answer) => {
        setMine(answer);
        setHelpful(answer.helpful);
        setComment(answer.comment ?? "");
      })
      .catch(() => undefined);
  }, [articleId, csrfToken]);

  async function send(value: boolean, event?: FormEvent) {
    event?.preventDefault();
    setHelpful(value);
    try {
      const saved = await putJson<MyFeedback>(
        `/v1/knowledge/published/${articleId}/feedback`,
        { helpful: value, comment: comment.trim() || null, visitorId: csrfToken ? null : visitorId() },
        csrfToken ?? "",
      );
      setMine(saved);
      setStatus("saved");
    } catch {
      setStatus("failed");
    }
  }

  return (
    <section aria-labelledby={`${id}-title`} className="space-y-3 rounded-md border border-line p-4" data-testid="feedback-box">
      <h2 id={`${id}-title`} className="text-lg font-semibold">{t("question")}</h2>
      <div className="flex flex-wrap gap-2" role="group" aria-labelledby={`${id}-title`}>
        <button
          type="button"
          aria-pressed={helpful === true}
          onClick={() => send(true)}
          className={`rounded-md border px-4 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand ${helpful === true ? "border-brand bg-brand text-surface" : "border-line"}`}
        >
          {t("yes")}
        </button>
        <button
          type="button"
          aria-pressed={helpful === false}
          onClick={() => send(false)}
          className={`rounded-md border px-4 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand ${helpful === false ? "border-brand bg-brand text-surface" : "border-line"}`}
        >
          {t("no")}
        </button>
      </div>
      {helpful !== null && (
        <form onSubmit={(e) => send(helpful, e)} className="space-y-2">
          <label htmlFor={`${id}-comment`} className="font-semibold">{t(helpful ? "commentYes" : "commentNo")}</label>
          <textarea id={`${id}-comment`} maxLength={1000} rows={3} value={comment} onChange={(e) => setComment(e.target.value)} className="block w-full rounded-md border border-line bg-surface px-3 py-2" />
          <button type="submit" className="rounded-md border border-line px-4 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">{t("sendComment")}</button>
        </form>
      )}
      <p role="status" aria-live="polite" className={status === "failed" ? "text-bad" : "text-ok"}>
        {status === "saved" ? t("thanks") : status === "failed" ? t("failed") : ""}
      </p>
      {mine && <p className="text-sm text-ink-muted">{t("totals", { count: mine.helpfulCount })}</p>}
    </section>
  );
}

"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { ApiError, deleteRequest, getJson, postJson, putJson } from "@/lib/client-api";
import type { TreeNode } from "@/lib/directory";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { Page } from "@/lib/integrations";
import type { ArticleDetail, ArticleVersion, FeedbackEntry } from "@/lib/knowledge";
import { dangerButton, primaryButton, secondaryButton } from "../settings/integrations/ui";
import { ArticleEditor, type ArticleContent } from "./ArticleEditor";
import { MarkdownView } from "./MarkdownView";

/** Staff view of one article: state, the live version, the draft or version in review, review actions, history and feedback. */
export function ArticleWorkspace({
  initial,
  categories,
  display,
  csrfToken,
}: {
  initial: ArticleDetail;
  categories: TreeNode[];
  display: DateDisplayOptions;
  csrfToken: string;
}) {
  const t = useTranslations("knowledge.workspace");
  const tStates = useTranslations("knowledge.states");
  const tVisibility = useTranslations("knowledge.visibility");
  const tErrors = useTranslations("knowledge.errors");
  const router = useRouter();
  const locale = display.locale as Locale;
  const [detail, setDetail] = useState(initial);
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const { article, published, working, actions } = detail;
  const base = `/v1/knowledge/articles/${article.id}`;
  const date = (value: string) => formatDateTime(new Date(value), display);
  const title = inLanguage(article.title, locale);

  function problem(refused: unknown): string {
    const code = refused instanceof ApiError ? refused.code : null;
    const known = ["own-article", "article-in-review", "version-mismatch", "not-in-review", "no-draft", "rate-limited", "validation-failed"];
    return tErrors(code && known.includes(code) ? code : "other");
  }

  async function run(action: () => Promise<ArticleDetail | null>, done: string) {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const next = await action();
      if (next) {
        setDetail(next);
      }
      setNotice(done);
      return true;
    } catch (refused) {
      setError(problem(refused));
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function save(content: ArticleContent) {
    if (await run(() => putJson<ArticleDetail>(`${base}/draft`, content, csrfToken), t("saved"))) {
      setEditing(false);
    }
  }

  async function discard() {
    if (!window.confirm(t("discardConfirm"))) return;
    setBusy(true);
    try {
      await deleteRequest(`${base}/draft`, csrfToken);
      if (!published && article.state !== "Retired") {
        router.push("/knowledge");
        return;
      }
      setDetail(await getJson<ArticleDetail>(base));
      setNotice(t("discarded"));
    } catch (refused) {
      setError(problem(refused));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-6" data-testid="article-workspace">
      <header className="space-y-2">
        <p className="text-sm text-ink-muted">
          <Link href="/knowledge" className="text-brand underline">{t("back")}</Link>
        </p>
        <h1 className="text-2xl font-bold" lang={title.lang} dir={title.dir}>
          <span dir="ltr" className="me-2 text-ink-muted">{article.number}</span>
          {title.value || t("untitled")}
        </h1>
        <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
          <div className="flex gap-1"><dt className="font-semibold">{t("state")}:</dt><dd data-testid="article-state">{tStates(article.state)}</dd></div>
          <div className="flex gap-1"><dt className="font-semibold">{t("visibility")}:</dt><dd>{tVisibility(article.visibility)}</dd></div>
          {working && <div className="flex gap-1"><dt className="font-semibold">{t("pending")}:</dt><dd data-testid="pending-state">{t(`pendingStates.${working.state}`, { number: working.number })}</dd></div>}
          <div className="flex gap-1"><dt className="font-semibold">{t("views")}:</dt><dd>{formatNumber(article.viewCount, display.locale, display.digits)}</dd></div>
          <div className="flex gap-1"><dt className="font-semibold">{t("helpful")}:</dt><dd>{t("helpfulValue", { yes: formatNumber(article.helpfulCount, display.locale, display.digits), no: formatNumber(article.notHelpfulCount, display.locale, display.digits) })}</dd></div>
          {detail.sourceTicket && (
            <div className="flex gap-1"><dt className="font-semibold">{t("fromTicket")}:</dt><dd><Link href={`/incidents/${detail.sourceTicket.id}`} className="text-brand underline" dir="ltr">{detail.sourceTicket.number}</Link></dd></div>
          )}
        </dl>
      </header>

      <p role="status" aria-live="polite" className="text-ok">{notice ?? ""}</p>
      {error && <p role="alert" className="text-bad">{error}</p>}

      {working?.reviewComment && working.state === "Draft" && (
        <p role="note" className="rounded-md border border-bad p-3" data-testid="review-comment">
          <span className="font-semibold">{t("sentBack", { name: working.reviewedBy?.name ?? "" })}</span> {working.reviewComment}
        </p>
      )}

      <div className="flex flex-wrap gap-2">
        {actions.edit && !editing && <button type="button" onClick={() => setEditing(true)} className={secondaryButton}>{working ? t("editDraft") : t("change")}</button>}
        {actions.submit && !editing && (
          <button type="button" disabled={busy} onClick={() => run(() => postJson<ArticleDetail>(`${base}/submit`, undefined, csrfToken), t("submitted"))} className={primaryButton}>
            {t("submit")}
          </button>
        )}
        {actions.retire && (
          <button type="button" disabled={busy} onClick={() => window.confirm(t("retireConfirm")) && run(() => postJson<ArticleDetail>(`${base}/retire`, undefined, csrfToken), t("retired"))} className={dangerButton}>
            {t("retire")}
          </button>
        )}
        {actions.reopen && <button type="button" disabled={busy} onClick={() => run(() => postJson<ArticleDetail>(`${base}/reopen`, undefined, csrfToken), t("reopened"))} className={secondaryButton}>{t("reopen")}</button>}
        {actions.discard && working && !editing && <button type="button" disabled={busy} onClick={discard} className={dangerButton}>{published || article.state === "Retired" ? t("discardChanges") : t("deleteDraft")}</button>}
        {published && article.state === "Published" && (
          <Link href={article.visibility === "External" ? `/help/articles/${article.id}` : `/knowledge/${article.id}/read`} className={secondaryButton}>
            {t("openPublished")}
          </Link>
        )}
      </div>

      {editing && (
        <ArticleEditor
          initial={working ?? published}
          articleId={article.id}
          images={detail.images}
          categories={categories}
          locale={locale}
          saving={busy}
          error={error}
          onSave={save}
          onImageAdded={(image) => setDetail({ ...detail, images: [...detail.images, image] })}
          csrfToken={csrfToken}
          submitLabel={t("saveDraft")}
        />
      )}

      {actions.review && working && (
        <ReviewBox
          busy={busy}
          onApprove={(comment) => run(() => postJson<ArticleDetail>(`${base}/approve`, { comment }, csrfToken), t("approved"))}
          onReject={(comment) => run(() => postJson<ArticleDetail>(`${base}/reject`, { comment }, csrfToken), t("rejected"))}
        />
      )}
      {!actions.review && working?.state === "InReview" && <p className="text-sm text-ink-muted">{t("waitingForReview")}</p>}

      {!editing && working && <VersionPanel heading={t("workingHeading", { number: working.number })} version={working} articleId={article.id} locale={locale} date={date} />}
      {published && <VersionPanel heading={t("publishedHeading", { number: published.number })} version={published} articleId={article.id} locale={locale} date={date} />}

      <History articleId={article.id} date={date} />
      <Feedback articleId={article.id} date={date} />
    </div>
  );
}

function ReviewBox({ busy, onApprove, onReject }: { busy: boolean; onApprove: (comment: string | null) => void; onReject: (comment: string) => void }) {
  const t = useTranslations("knowledge.workspace");
  const id = useId();
  const [comment, setComment] = useState("");
  const [missing, setMissing] = useState(false);

  function reject(event: FormEvent) {
    event.preventDefault();
    if (!comment.trim()) {
      setMissing(true);
      return;
    }
    setMissing(false);
    onReject(comment.trim());
  }

  return (
    <form onSubmit={reject} className="space-y-3 rounded-md border border-brand p-4" aria-labelledby={`${id}-title`} data-testid="review-box">
      <h2 id={`${id}-title`} className="text-lg font-semibold">{t("reviewHeading")}</h2>
      <p className="text-sm">{t("reviewHelp")}</p>
      <div>
        <label htmlFor={`${id}-comment`} className="font-semibold">{t("reviewComment")}</label>
        <textarea id={`${id}-comment`} rows={3} maxLength={1000} value={comment} onChange={(e) => setComment(e.target.value)} aria-invalid={missing} aria-describedby={missing ? `${id}-missing` : undefined} className="block w-full rounded-md border border-line bg-surface px-3 py-2" />
        {missing && <p id={`${id}-missing`} className="text-bad">{t("commentRequired")}</p>}
      </div>
      <div className="flex flex-wrap gap-2">
        <button type="button" disabled={busy} onClick={() => onApprove(comment.trim() || null)} className={primaryButton}>{t("approve")}</button>
        <button type="submit" disabled={busy} className={dangerButton}>{t("reject")}</button>
      </div>
    </form>
  );
}

function VersionPanel({ heading, version, articleId, locale, date }: { heading: string; version: ArticleVersion; articleId: string; locale: Locale; date: (v: string) => string }) {
  const t = useTranslations("knowledge.workspace");
  const [language, setLanguage] = useState<Locale>(version.body[locale] ? locale : locale === "ar" ? "en" : "ar");
  const id = useId();
  const dir = language === "ar" ? "rtl" : "ltr";
  return (
    <section aria-labelledby={`${id}-title`} className="space-y-3 rounded-md border border-line p-4" data-testid={`version-${version.state}`}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={`${id}-title`} className="text-lg font-semibold">{heading}</h2>
        <div role="group" aria-label={t("showLanguage")} className="flex gap-1">
          {(["en", "ar"] as const).map((l) => (
            <button key={l} type="button" aria-pressed={language === l} onClick={() => setLanguage(l)} className={`rounded border px-2 py-1 text-sm ${language === l ? "border-brand font-semibold" : "border-line"}`}>
              {t(`language.${l}`)}
            </button>
          ))}
        </div>
      </div>
      <p className="text-sm text-ink-muted">
        {t("versionBy", { name: version.lastEditedBy?.name ?? version.createdBy?.name ?? "—", date: date(version.updatedAt) })}
        {version.changeNote && <> · {version.changeNote}</>}
      </p>
      {version.title[language] ? (
        <>
          <h3 className="text-xl font-bold" lang={language} dir={dir}>{version.title[language]}</h3>
          {version.summary[language] && <p lang={language} dir={dir} className="text-ink-muted">{version.summary[language]}</p>}
          <MarkdownView markdown={version.body[language] ?? ""} articleId={articleId} lang={language} dir={dir} />
        </>
      ) : (
        <p className="text-bad">{t("languageMissing")}</p>
      )}
    </section>
  );
}

function History({ articleId, date }: { articleId: string; date: (v: string) => string }) {
  const t = useTranslations("knowledge.workspace");
  const tVersions = useTranslations("knowledge.versionStates");
  const [versions, setVersions] = useState<ArticleVersion[] | null>(null);
  return (
    <details className="rounded-md border border-line p-4" onToggle={(e) => (e.currentTarget.open && !versions ? void getJson<ArticleVersion[]>(`/v1/knowledge/articles/${articleId}/versions`).then(setVersions).catch(() => setVersions([])) : undefined)}>
      <summary className="cursor-pointer font-semibold">{t("history")}</summary>
      {versions === null ? (
        <p>{t("loading")}</p>
      ) : (
        <ol className="mt-3 space-y-2">
          {versions.map((v) => (
            <li key={v.id} className="text-sm">
              <span className="font-semibold">{t("versionNumber", { number: v.number })}</span> · {tVersions(v.state)} · {date(v.updatedAt)}
              {v.reviewedBy && <> · {t("reviewedBy", { name: v.reviewedBy.name ?? "" })}</>}
              {v.reviewComment && <span className="block text-ink-muted">“{v.reviewComment}”</span>}
            </li>
          ))}
        </ol>
      )}
    </details>
  );
}

function Feedback({ articleId, date }: { articleId: string; date: (v: string) => string }) {
  const t = useTranslations("knowledge.workspace");
  const [page, setPage] = useState<Page<FeedbackEntry> | null>(null);
  return (
    <details className="rounded-md border border-line p-4" onToggle={(e) => (e.currentTarget.open && !page ? void getJson<Page<FeedbackEntry>>(`/v1/knowledge/articles/${articleId}/feedback?pageSize=50`).then(setPage).catch(() => setPage({ items: [], page: 1, pageSize: 50, totalCount: 0 })) : undefined)}>
      <summary className="cursor-pointer font-semibold">{t("feedback")}</summary>
      {page === null ? (
        <p>{t("loading")}</p>
      ) : page.items.length === 0 ? (
        <p className="mt-3">{t("noFeedback")}</p>
      ) : (
        <ul className="mt-3 space-y-2">
          {page.items.map((f) => (
            <li key={f.id} className="text-sm">
              <span className={f.helpful ? "font-semibold text-ok" : "font-semibold text-bad"}>{f.helpful ? t("wasHelpful") : t("wasNotHelpful")}</span>
              {" · "}{f.visitor ? t("visitor") : f.userName ?? "—"} · {date(f.updatedAt)}
              {f.comment && <span className="block" dir="auto">{f.comment}</span>}
            </li>
          ))}
        </ul>
      )}
    </details>
  );
}

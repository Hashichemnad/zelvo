"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { getJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { Page } from "@/lib/integrations";
import { articleStates, type ArticleState, type ArticleSummary } from "@/lib/knowledge";
import { cellClass, inputClass, secondaryButton, tableClass } from "../settings/integrations/ui";

const pageSize = 25;

/** Staff list of articles in every state, with filters. */
export function KnowledgeList({ initial, display }: { initial: Page<ArticleSummary>; display: DateDisplayOptions }) {
  const t = useTranslations("knowledge.list");
  const tStates = useTranslations("knowledge.states");
  const tVisibility = useTranslations("knowledge.visibility");
  const id = useId();
  const locale = display.locale as Locale;
  const [page, setPage] = useState(initial);
  const [state, setState] = useState<ArticleState | "">("");
  const [visibility, setVisibility] = useState("");
  const [text, setText] = useState("");
  const [mine, setMine] = useState(false);
  const [failed, setFailed] = useState(false);
  const pages = Math.max(1, Math.ceil(page.totalCount / pageSize));
  const number = (v: number) => formatNumber(v, display.locale, display.digits);

  async function load(n: number, filters = { state, visibility, text, mine }) {
    const query = new URLSearchParams({ page: String(n), pageSize: String(pageSize) });
    if (filters.state) query.set("state", filters.state);
    if (filters.visibility) query.set("visibility", filters.visibility);
    if (filters.text.trim()) query.set("text", filters.text.trim());
    if (filters.mine) query.set("mine", "true");
    try {
      setPage(await getJson<Page<ArticleSummary>>(`/v1/knowledge/articles?${query}`));
      setFailed(false);
    } catch {
      setFailed(true);
    }
  }

  return (
    <div className="space-y-4" data-testid="knowledge-list">
      <form
        role="search"
        className="flex flex-wrap items-end gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          void load(1);
        }}
      >
        <div>
          <label htmlFor={`${id}-text`} className="font-semibold">{t("text")}</label>
          <input id={`${id}-text`} type="search" value={text} onChange={(e) => setText(e.target.value)} className={inputClass} />
        </div>
        <div>
          <label htmlFor={`${id}-state`} className="font-semibold">{t("state")}</label>
          <select id={`${id}-state`} value={state} onChange={(e) => setState(e.target.value as ArticleState | "")} className={inputClass}>
            <option value="">{t("all")}</option>
            {articleStates.map((s) => (
              <option key={s} value={s}>{tStates(s)}</option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor={`${id}-visibility`} className="font-semibold">{t("visibility")}</label>
          <select id={`${id}-visibility`} value={visibility} onChange={(e) => setVisibility(e.target.value)} className={inputClass}>
            <option value="">{t("all")}</option>
            <option value="Internal">{tVisibility("Internal")}</option>
            <option value="External">{tVisibility("External")}</option>
          </select>
        </div>
        <label className="flex items-center gap-2 pb-2">
          <input type="checkbox" checked={mine} onChange={(e) => setMine(e.target.checked)} />
          {t("mine")}
        </label>
        <button type="submit" className={secondaryButton}>{t("filter")}</button>
      </form>
      <p role="status" aria-live="polite" className="text-bad">{failed ? t("failed") : ""}</p>
      {page.items.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass} aria-label={t("label")}>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("article")}</th>
                <th scope="col" className={cellClass}>{t("state")}</th>
                <th scope="col" className={cellClass}>{t("visibility")}</th>
                <th scope="col" className={cellClass}>{t("usage")}</th>
                <th scope="col" className={cellClass}>{t("updated")}</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((a) => {
                const title = inLanguage(a.title, locale);
                return (
                  <tr key={a.id} data-testid="knowledge-row">
                    <td className={cellClass}>
                      <span className="block text-xs text-ink-muted" dir="ltr">{a.number}</span>
                      <Link href={`/knowledge/${a.id}`} className="font-semibold text-brand underline" lang={title.lang} dir={title.dir}>
                        {title.value || t("untitled")}
                      </Link>
                      {(a.title.missing?.length ?? 0) > 0 && <span className="block text-xs text-bad">{t("missingLanguage")}</span>}
                    </td>
                    <td className={cellClass}>
                      {tStates(a.state)}
                      {a.pendingState && a.state === "Published" && <span className="block text-xs text-ink-muted">{t(a.pendingState === "InReview" ? "changesInReview" : "changesDraft")}</span>}
                    </td>
                    <td className={cellClass}>{tVisibility(a.visibility)}</td>
                    <td className={cellClass}>{t("usageValue", { views: number(a.viewCount), yes: number(a.helpfulCount), no: number(a.notHelpfulCount) })}</td>
                    <td className={cellClass}>{formatDateTime(new Date(a.updatedAt), display)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      <nav className="flex flex-wrap items-center gap-3" aria-label={t("pages")}>
        <button type="button" disabled={page.page <= 1} onClick={() => load(page.page - 1)} className={secondaryButton}>{t("previous")}</button>
        <span>{t("pageOf", { page: number(page.page), pages: number(pages) })}</span>
        <button type="button" disabled={page.page >= pages} onClick={() => load(page.page + 1)} className={secondaryButton}>{t("next")}</button>
      </nav>
    </div>
  );
}

/** Articles waiting for review, oldest first. */
export function ReviewQueue({ items, display }: { items: ArticleSummary[]; display: DateDisplayOptions }) {
  const t = useTranslations("knowledge.review");
  const locale = display.locale as Locale;
  if (items.length === 0) {
    return <p data-testid="review-empty">{t("empty")}</p>;
  }
  return (
    <ul className="space-y-3" data-testid="review-queue">
      {items.map((a) => {
        const title = inLanguage(a.title, locale);
        return (
          <li key={a.id} className="rounded-md border border-line p-4" data-testid="review-item">
            <Link href={`/knowledge/${a.id}`} className="font-semibold text-brand underline" lang={title.lang} dir={title.dir}>
              {title.value}
            </Link>
            <p className="text-sm text-ink-muted">
              <span dir="ltr">{a.number}</span>
              {a.submittedAt && <> · {t("submitted", { date: formatDateTime(new Date(a.submittedAt), display) })}</>}
              {!a.canReview && <> · {t("ownArticle")}</>}
            </p>
          </li>
        );
      })}
    </ul>
  );
}

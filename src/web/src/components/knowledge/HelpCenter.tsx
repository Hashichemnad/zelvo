"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { getJson } from "@/lib/client-api";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { Page } from "@/lib/integrations";
import type { PublishedSummary } from "@/lib/knowledge";

/**
 * The portal's knowledge base: search and the most read external articles. A building block of the
 * self-service portal's search-first home (task D2).
 */
export function HelpCenter({ initial, locale }: { initial: Page<PublishedSummary>; locale: Locale }) {
  const t = useTranslations("knowledge.help");
  const id = useId();
  const [text, setText] = useState("");
  const [page, setPage] = useState(initial);
  const [searched, setSearched] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  async function search(event: FormEvent) {
    event.preventDefault();
    setFailed(false);
    try {
      const query = new URLSearchParams({ pageSize: "25" });
      if (text.trim()) query.set("text", text.trim());
      setPage(await getJson<Page<PublishedSummary>>(`/v1/knowledge/published?${query}`));
      setSearched(text.trim() || null);
    } catch {
      setFailed(true);
    }
  }

  return (
    <div className="space-y-6" data-testid="help-center">
      <form role="search" onSubmit={search} className="flex flex-wrap items-end gap-2">
        <div className="min-w-64 grow">
          <label htmlFor={`${id}-q`} className="font-semibold">{t("searchLabel")}</label>
          <input id={`${id}-q`} type="search" maxLength={200} value={text} onChange={(e) => setText(e.target.value)} className="block w-full rounded-md border border-line bg-surface px-3 py-2" />
        </div>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">{t("search")}</button>
      </form>
      <p role="status" aria-live="polite">
        {failed ? t("failed") : searched ? t("results", { count: page.totalCount, text: searched }) : ""}
      </p>
      {page.items.length === 0 ? (
        <p>{searched ? t("noResults") : t("empty")}</p>
      ) : (
        <ul className="space-y-3" aria-label={searched ? t("resultsLabel") : t("popular")}>
          {page.items.map((article) => {
            const title = inLanguage(article.title, locale);
            const summary = inLanguage(article.summary, locale);
            return (
              <li key={article.id} className="rounded-md border border-line p-4" data-testid="help-article">
                <Link href={`/help/articles/${article.id}`} className="text-lg font-semibold text-brand underline-offset-4 hover:underline" lang={title.lang} dir={title.dir}>
                  {title.value}
                </Link>
                {summary.value && <p className="text-sm text-ink-muted" lang={summary.lang} dir={summary.dir}>{summary.value}</p>}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

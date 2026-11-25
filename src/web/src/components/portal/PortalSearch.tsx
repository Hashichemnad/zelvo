"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { getJson } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import type { DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import type { PortalSearchResult } from "@/lib/portal";
import { TicketProgressCard } from "./TicketProgress";

/** Shortest text that is searched as the person types. */
const minimumLength = 2;

/**
 * "How can we help?" (module 4): one search across the catalog, the knowledge base and the
 * person's own tickets, as they type (and on Enter). Results come in three labelled groups; the
 * number found is announced politely. Nothing found leads to raising a ticket.
 */
export function PortalSearch({ initialQuery, display, signedIn }: { initialQuery: string; display: DateDisplayOptions; signedIn: boolean }) {
  const t = useTranslations("portal.search");
  const locale = useLocale() as Locale;
  const id = useId();
  const [query, setQuery] = useState(initialQuery);
  const [result, setResult] = useState<PortalSearchResult | null>(null);
  const [status, setStatus] = useState<"idle" | "searching" | "done" | "unavailable">("idle");
  const latest = useRef(0);

  useEffect(() => {
    const q = query.trim();
    const run = ++latest.current;
    const timer = setTimeout(() => {
      if (q.length < minimumLength) {
        setResult(null);
        setStatus("idle");
        return;
      }
      setStatus("searching");
      getJson<PortalSearchResult>(`/v1/portal/search?${new URLSearchParams({ q })}`)
        .then((found) => {
          if (run !== latest.current) return;
          setResult(found);
          setStatus("done");
        })
        .catch(() => {
          if (run !== latest.current) return;
          setResult(null);
          setStatus("unavailable");
        });
    }, 300);
    return () => clearTimeout(timer);
  }, [query]);

  const total = result ? result.services.length + result.articles.length + result.tickets.length : 0;
  const group = (key: "services" | "articles", heading: string) =>
    result && result[key].length > 0 ? (
      <section aria-labelledby={`${id}-${key}`} className="space-y-2">
        <h3 id={`${id}-${key}`} className="font-semibold">{heading}</h3>
        <ul className="space-y-1">
          {result[key].map((hit) => (
            <li key={hit.id}>
              <Link href={hit.path} className="text-brand underline" dir="auto">{textOf(hit.title, locale)}</Link>
            </li>
          ))}
        </ul>
      </section>
    ) : null;

  return (
    <div className="space-y-4">
      <form role="search" aria-label={t("label")} onSubmit={(e) => e.preventDefault()} className="flex flex-col gap-2 sm:flex-row" data-testid="portal-search">
        <label htmlFor={`${id}-q`} className="sr-only">{t("label")}</label>
        <input id={`${id}-q`} type="search" name="q" value={query} onChange={(e) => setQuery(e.target.value)} maxLength={200} dir="auto" autoComplete="off"
          placeholder={t("placeholder")} aria-describedby={`${id}-hint`}
          className="min-h-12 flex-1 rounded-lg border border-line bg-surface px-4 py-3 text-lg text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand" />
        <button type="submit" className="min-h-12 rounded-lg bg-brand px-6 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
          {t("button")}
        </button>
      </form>
      <p id={`${id}-hint`} className="text-sm text-ink-muted">{t(signedIn ? "hint" : "hintVisitor")}</p>
      <p role="status" aria-live="polite" className="text-sm">
        {status === "searching" ? t("searching") : status === "unavailable" ? t("unavailable") : status === "done" ? t("found", { count: total }) : ""}
      </p>
      {status === "done" && result && (
        <div className="space-y-6 rounded-lg border border-line bg-surface p-4" data-testid="portal-results">
          {total === 0 && (
            <p>{t("nothing")} <Link href="/my-incidents/new" className="font-semibold text-brand underline">{t("raise")}</Link></p>
          )}
          {result.tickets.length > 0 && (
            <section aria-labelledby={`${id}-tickets`} className="space-y-2">
              <h3 id={`${id}-tickets`} className="font-semibold">{t("tickets")}</h3>
              <ul className="space-y-3">
                {result.tickets.map((ticket) => <li key={ticket.id}><TicketProgressCard ticket={ticket} display={display} /></li>)}
              </ul>
            </section>
          )}
          <div className="grid gap-6 md:grid-cols-2">
            {group("services", t("services"))}
            {group("articles", t("articles"))}
          </div>
        </div>
      )}
    </div>
  );
}

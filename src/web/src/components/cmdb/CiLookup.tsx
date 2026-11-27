"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldButton, fieldInput } from "@/components/forms/ui";
import { getJson } from "@/lib/client-api";
import { textIn, type CiPick } from "@/lib/cmdb";
import type { Locale } from "@/lib/incidents";

/** `/cmdb/lookup` for the words, limited to the classes (each with the classes below it). */
export function lookupPath(q: string, classIds: readonly string[] = [], ids: readonly string[] = []): string {
  const query = new URLSearchParams();
  if (q) query.set("q", q);
  for (const id of classIds) query.append("classIds", id);
  for (const id of ids) query.append("ids", id);
  if (ids.length > 0) query.set("limit", String(Math.min(50, ids.length)));
  return `/v1/cmdb/lookup?${query}`;
}

/**
 * Finds a configuration item by number or name and picks it (task G1): a labelled search box, a
 * search button (Enter also searches), the number of results read out, and one button per item.
 * Used by the form field, the ticket panel and relationships.
 */
export function CiLookup({
  id,
  label,
  describedBy,
  invalid,
  classIds = [],
  exclude = [],
  disabled,
  onPick,
}: {
  id: string;
  /** Label of the search box; shown above it. */
  label: string;
  describedBy?: string;
  invalid?: boolean;
  classIds?: readonly string[];
  /** Items not offered (already picked, the item itself). */
  exclude?: readonly string[];
  disabled?: boolean;
  onPick: (item: CiPick) => void;
}) {
  const t = useTranslations("cmdb.picker");
  const locale = useLocale() as Locale;
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<CiPick[] | null>(null);
  const [failed, setFailed] = useState(false);
  const shown = results?.filter((r) => !exclude.includes(r.id)) ?? null;

  async function search() {
    setFailed(false);
    try {
      setResults(await getJson<CiPick[]>(lookupPath(query.trim(), classIds)));
    } catch {
      setResults(null);
      setFailed(true);
    }
  }

  return (
    <div className="space-y-2">
      <label htmlFor={id} className="block text-sm font-semibold">{label}</label>
      <div className="flex flex-wrap gap-2">
        <input id={id} type="search" dir="auto" value={query} disabled={disabled} aria-describedby={describedBy} aria-invalid={invalid || undefined}
          placeholder={t("searchPlaceholder")} maxLength={200} onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              if (!disabled) void search();
            }
          }}
          className={`${fieldInput} max-w-xs`} />
        <button type="button" className={fieldButton} disabled={disabled} onClick={() => void search()}>
          {t("search")}
        </button>
      </div>
      <p role="status" aria-live="polite" className="text-sm">
        {failed ? t("searchFailed") : shown ? t("results", { count: shown.length }) : ""}
      </p>
      {shown && shown.length > 0 && (
        <ul className="space-y-1">
          {shown.map((item) => {
            const name = `${item.number} ${item.name}`;
            return (
              <li key={item.id}>
                <button type="button" className={`${fieldButton} text-start`}
                  onClick={() => {
                    onPick(item);
                    setResults(null);
                    setQuery("");
                  }}>
                  {t("pick", { name })}
                  <span className="ms-2 font-normal text-ink-muted">{textIn(item.class.path, locale) || textIn(item.class.name, locale)}</span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

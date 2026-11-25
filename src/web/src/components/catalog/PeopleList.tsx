"use client";

import { useEffect, useId, useState } from "react";
import { useTranslations } from "next-intl";
import { smallButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { getJson } from "@/lib/client-api";
import type { Person } from "@/lib/incidents";

/** People chosen by searching for them (service owner, access, approvers). */
export function PeopleList({ legend, ids, onChange, single, idPrefix }: { legend: string; ids: string[]; onChange: (ids: string[]) => void; single?: boolean; idPrefix?: string }) {
  const t = useTranslations("catalog.editor");
  const generated = useId();
  const id = idPrefix ?? generated;
  const [names, setNames] = useState<Record<string, string | null>>({});
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<Person[]>([]);
  const missingKey = ids.filter((x) => !(x in names)).join(",");
  useEffect(() => {
    if (!missingKey) return;
    const missing = missingKey.split(",");
    let cancelled = false;
    getJson<Person[]>(`/v1/forms/people?${new URLSearchParams({ ids: missingKey })}`)
      .then((people) => { if (!cancelled) setNames((n) => ({ ...n, ...Object.fromEntries(missing.map((m) => [m, people.find((p) => p.id === m)?.displayName ?? null])) })); })
      .catch(() => { if (!cancelled) setNames((n) => ({ ...n, ...Object.fromEntries(missing.map((m) => [m, null])) })); });
    return () => { cancelled = true; };
  }, [missingKey]);
  return (
    <fieldset className="space-y-2">
      <legend className="font-semibold">{legend}</legend>
      <ul className="space-y-1">
        {ids.map((personId) => (
          <li key={personId} className="flex items-center gap-2">
            <span>{names[personId] ?? personId}</span>
            <button type="button" className={smallButton} onClick={() => onChange(ids.filter((x) => x !== personId))}
              aria-label={t("removePerson", { name: names[personId] ?? "" })}>{t("remove")}</button>
          </li>
        ))}
      </ul>
      <div className="flex flex-wrap items-end gap-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-q`} className="block text-sm">{t("findPerson")}</label>
          <input id={`${id}-q`} type="search" value={query} onChange={(e) => setQuery(e.target.value)} className={fieldInput} />
        </div>
        <button type="button" className={smallButton} disabled={query.trim().length < 2}
          onClick={() => void getJson<Person[]>(`/v1/forms/people?${new URLSearchParams({ q: query.trim() })}`).then(setFound).catch(() => setFound([]))}>{t("find")}</button>
      </div>
      {found.length > 0 && (
        <ul className="space-y-1">
          {found.map((p) => (
            <li key={p.id}>
              <button type="button" className={smallButton} disabled={ids.includes(p.id)}
                onClick={() => {
                  setNames((n) => ({ ...n, [p.id]: p.displayName }));
                  onChange(single ? [p.id] : [...ids, p.id]);
                  setFound([]);
                  setQuery("");
                }}>{t("choosePerson", { name: p.displayName ?? p.id })}</button>
            </li>
          ))}
        </ul>
      )}
    </fieldset>
  );
}

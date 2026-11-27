"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { getJson } from "@/lib/client-api";
import type { AssetPerson, AssetState } from "@/lib/assets";
import type { UserPage } from "@/lib/directory";
import { fieldButton, fieldInput } from "@/components/forms/ui";

/** An asset's state as a badge; the word says it, the colour only helps (WCAG 1.4.1). */
export function AssetStateBadge({ state }: { state: AssetState }) {
  const t = useTranslations("assets.states");
  const tone =
    state === "Assigned" || state === "InStock"
      ? "border-ok text-ok"
      : state === "InRepair"
        ? "border-warn text-warn"
        : state === "Retired" || state === "Disposed"
          ? "border-line text-ink-muted"
          : "border-brand text-brand";
  return <span className={`inline-block rounded-full border px-2 text-xs font-semibold ${tone}`}>{t(state)}</span>;
}

/** Finds an active person by name or email and picks them (assignment, contract owners). */
export function PersonSearch({ label, onPick, disabled }: { label: string; onPick: (person: AssetPerson) => void; disabled?: boolean }) {
  const t = useTranslations("assets.person");
  const id = useId();
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<AssetPerson[] | null>(null);
  const [failed, setFailed] = useState(false);

  async function search() {
    setFailed(false);
    try {
      const page = await getJson<UserPage>(`/v1/users?${new URLSearchParams({ q: query.trim(), status: "Active", pageSize: "10" })}`);
      setFound(page.items.map((u) => ({ id: u.id, displayName: u.displayName ?? u.email ?? null })));
    } catch {
      setFound(null);
      setFailed(true);
    }
  }

  return (
    <div className="space-y-2">
      <label htmlFor={id} className="block text-sm font-semibold">{label}</label>
      <div className="flex flex-wrap gap-2">
        <input id={id} type="search" dir="auto" value={query} maxLength={200} disabled={disabled} onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              void search();
            }
          }}
          className={`${fieldInput} max-w-xs`} />
        <button type="button" className={fieldButton} disabled={disabled || !query.trim()} onClick={() => void search()}>{t("search")}</button>
      </div>
      <p role="status" aria-live="polite" className="text-sm">{failed ? t("failed") : found ? t("results", { count: found.length }) : ""}</p>
      {found && found.length > 0 && (
        <ul className="space-y-1">
          {found.map((p) => (
            <li key={p.id}>
              <button type="button" className={`${fieldButton} text-start`} onClick={() => { onPick(p); setFound(null); setQuery(""); }}>
                <span dir="auto">{p.displayName ?? p.id}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

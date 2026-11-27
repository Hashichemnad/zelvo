"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { CiLookup } from "@/components/cmdb/CiLookup";
import { fieldInput } from "@/components/forms/ui";
import { primaryButton } from "@/components/settings/integrations/ui";
import { ApiError, postJson } from "@/lib/client-api";
import { assetStates, tagPattern, type Asset, type AssetPerson, type AssetState } from "@/lib/assets";
import type { CiPick } from "@/lib/cmdb";
import { PersonSearch } from "./common";

/**
 * Make a hardware or software configuration item an asset (task G3, assets.manage): pick the item,
 * then a tag (or leave it empty for the next AST-000123), its state and, for assigned assets,
 * the person.
 */
export function NewAssetForm({ classIds, csrfToken }: { classIds: string[]; csrfToken: string }) {
  const t = useTranslations("assets.new");
  const tStates = useTranslations("assets.states");
  const router = useRouter();
  const id = useId();
  const [item, setItem] = useState<CiPick | null>(null);
  const [tag, setTag] = useState("");
  const [state, setState] = useState<AssetState>("InStock");
  const [person, setPerson] = useState<AssetPerson | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const tagInvalid = tag !== "" && !tagPattern.test(tag);

  async function save() {
    if (!item) return;
    setBusy(true);
    setError(null);
    try {
      const asset = await postJson<Asset>("/v1/assets", {
        configurationItemId: item.id, tag: tag || undefined, state, assignedToId: state === "Assigned" ? person?.id : undefined,
      }, csrfToken);
      router.push(`/assets/${asset.id}`);
    } catch (e) {
      setError(e instanceof ApiError && e.code && t.has(`errors.${e.code}`) ? t(`errors.${e.code}`) : t("errors.failed"));
      setBusy(false);
    }
  }

  return (
    <form className="max-w-2xl space-y-4 rounded-lg border border-line p-4" aria-labelledby={`${id}-heading`} onSubmit={(e) => { e.preventDefault(); void save(); }}>
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {item ? (
        <p className="font-semibold">
          <span dir="ltr" className="font-mono">{item.number}</span> <span dir="auto">{item.name}</span>{" "}
          <button type="button" className="text-sm text-brand underline" onClick={() => setItem(null)}>{t("otherItem")}</button>
        </p>
      ) : (
        <CiLookup id={`${id}-item`} label={t("item")} classIds={classIds} onPick={setItem} />
      )}
      <div className="space-y-1">
        <label htmlFor={`${id}-tag`} className="block font-semibold">{t("tag")}</label>
        <input id={`${id}-tag`} dir="ltr" maxLength={32} value={tag} aria-describedby={`${id}-tag-hint`} aria-invalid={tagInvalid || undefined}
          onChange={(e) => setTag(e.target.value.toUpperCase())} className={`${fieldInput} max-w-56 font-mono`} />
        <p id={`${id}-tag-hint`} className={`text-sm ${tagInvalid ? "font-semibold text-bad" : "text-ink-muted"}`}>{t("tagHint")}</p>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-state`} className="block font-semibold">{t("state")}</label>
        <select id={`${id}-state`} value={state} onChange={(e) => setState(e.target.value as AssetState)} className={`${fieldInput} max-w-56`}>
          {assetStates.filter((s) => s !== "Disposed").map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
        </select>
      </div>
      {state === "Assigned" && (
        person ? (
          <p>{t("assignedTo", { name: person.displayName ?? "" })} <button type="button" className="text-sm text-brand underline" onClick={() => setPerson(null)}>{t("otherPerson")}</button></p>
        ) : (
          <PersonSearch label={t("person")} onPick={setPerson} />
        )
      )}
      <button type="submit" className={primaryButton} disabled={busy || !item || tagInvalid || (state === "Assigned" && !person)}>{t("save")}</button>
      <p role="status" aria-live="polite" className="font-semibold text-bad">{error ?? ""}</p>
    </form>
  );
}

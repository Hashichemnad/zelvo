"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { fieldInput } from "@/components/forms/ui";
import { cellClass, primaryButton, secondaryButton, tableClass } from "@/components/settings/integrations/ui";
import { ApiError, deleteRequest, getJson, patchJson, postJson } from "@/lib/client-api";
import { contractKinds, normaliseTag, type AssetContract, type AssetContractKind, type AssetPerson, type AssetSummary } from "@/lib/assets";
import { formatNumber, type DateDisplayOptions } from "@/lib/format";
import { PersonSearch } from "./common";
import { WarrantyEnd } from "./AssetTable";

interface Draft {
  kind: AssetContractKind;
  name: string;
  reference: string;
  supplier: string;
  startsOn: string;
  endsOn: string;
  costAed: string;
  owner: AssetPerson | null;
  tags: string;
}

const empty: Draft = { kind: "Warranty", name: "", reference: "", supplier: "", startsOn: "", endsOn: "", costAed: "", owner: null, tags: "" };

/**
 * Contracts and warranties (task G3): soonest end first, ending soon and ended marked, with the
 * assets each covers (typed as asset tags). An alert goes to asset managers and the owner before
 * the end date.
 */
export function ContractsPanel({ initial, canManage, csrfToken, display, warningDays }: { initial: AssetContract[]; canManage: boolean; csrfToken: string; display: DateDisplayOptions; warningDays: number }) {
  const t = useTranslations("assets.contracts");
  const tKinds = useTranslations("assets.contractKinds");
  const id = useId();
  const [contracts, setContracts] = useState(initial);
  const [editing, setEditing] = useState<{ contract: AssetContract | null; draft: Draft } | null>(null);
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<Draft>) => editing && setEditing({ ...editing, draft: { ...editing.draft, ...patch } });

  async function open(contract: AssetContract) {
    const full = await getJson<AssetContract>(`/v1/assets/contracts/${contract.id}`);
    setEditing({
      contract: full,
      draft: {
        kind: full.kind, name: full.name, reference: full.reference ?? "", supplier: full.supplier ?? "", startsOn: full.startsOn ?? "", endsOn: full.endsOn,
        costAed: full.costAed?.toString() ?? "", owner: full.owner, tags: full.items.map((i) => i.tag).join(", "),
      },
    });
  }

  async function save() {
    if (!editing) return;
    const d = editing.draft;
    setBusy(true);
    setStatus(null);
    try {
      // Asset tags are looked up one by one; an unknown tag stops the save.
      const tags = d.tags.split(/[\s,;]+/).map(normaliseTag).filter((x) => x.length > 0);
      const itemIds: string[] = [];
      for (const tag of tags) {
        try {
          itemIds.push((await getJson<AssetSummary>(`/v1/assets/lookup?${new URLSearchParams({ tag })}`)).id);
        } catch {
          setStatus({ ok: false, text: t("unknownTag", { tag }) });
          return;
        }
      }
      const body = {
        kind: d.kind, name: d.name.trim(), reference: d.reference, supplier: d.supplier, startsOn: d.startsOn || "0001-01-01", endsOn: d.endsOn,
        costAed: d.costAed === "" ? undefined : Number(d.costAed), ownerId: d.owner?.id ?? "00000000-0000-0000-0000-000000000000", itemIds,
      };
      if (editing.contract) await patchJson(`/v1/assets/contracts/${editing.contract.id}`, body, csrfToken, { "If-Match": editing.contract.etag });
      else await postJson("/v1/assets/contracts", body, csrfToken);
      setContracts(await getJson<AssetContract[]>("/v1/assets/contracts"));
      setEditing(null);
      setStatus({ ok: true, text: t("saved") });
    } catch (e) {
      setStatus({ ok: false, text: e instanceof ApiError && e.code && t.has(`errors.${e.code}`) ? t(`errors.${e.code}`) : t("errors.failed") });
    } finally {
      setBusy(false);
    }
  }

  async function remove(contract: AssetContract) {
    if (!window.confirm(t("confirmDelete", { name: contract.name }))) return;
    try {
      await deleteRequest(`/v1/assets/contracts/${contract.id}`, csrfToken);
      setContracts((all) => all.filter((c) => c.id !== contract.id));
      setStatus({ ok: true, text: t("deleted") });
    } catch {
      setStatus({ ok: false, text: t("errors.failed") });
    }
  }

  const d = editing?.draft;
  return (
    <div className="space-y-4" data-testid="contracts-panel">
      {contracts.length === 0 ? <p>{t("none")}</p> : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("title")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("name")}</th>
                <th scope="col" className={cellClass}>{t("kind")}</th>
                <th scope="col" className={cellClass}>{t("endsOn")}</th>
                <th scope="col" className={cellClass}>{t("assets")}</th>
                {canManage && <th scope="col" className={cellClass}><span className="sr-only">{t("actions")}</span></th>}
              </tr>
            </thead>
            <tbody>
              {contracts.map((c) => (
                <tr key={c.id}>
                  <td className={cellClass}>
                    <span className="font-semibold" dir="auto">{c.name}</span>
                    {c.supplier && <span className="block text-sm text-ink-muted" dir="auto">{c.supplier}</span>}
                  </td>
                  <td className={cellClass}>{tKinds(c.kind)}</td>
                  <td className={cellClass}><WarrantyEnd endsOn={c.endsOn} warningDays={warningDays} display={display} /></td>
                  <td className={cellClass}>{formatNumber(c.itemCount, display.locale, display.digits)}</td>
                  {canManage && (
                    <td className={cellClass}>
                      <div className="flex flex-wrap gap-2">
                        <button type="button" className={secondaryButton} onClick={() => void open(c)}>{t("edit")}</button>
                        <button type="button" className={secondaryButton} onClick={() => void remove(c)}>{t("delete")}</button>
                      </div>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p role="status" aria-live="polite" className={status?.ok === false ? "font-semibold text-bad" : "text-ok"}>{status?.text ?? ""}</p>
      {canManage && !editing && <button type="button" className={primaryButton} onClick={() => setEditing({ contract: null, draft: empty })}>{t("add")}</button>}
      {editing && d && (
        <form className="max-w-2xl space-y-3 rounded-lg border border-line p-4" aria-labelledby={`${id}-form`} onSubmit={(e) => { e.preventDefault(); void save(); }}>
          <h2 id={`${id}-form`} className="text-lg font-semibold">{editing.contract ? t("editHeading", { name: editing.contract.name }) : t("addHeading")}</h2>
          <div className="flex flex-wrap gap-4">
            <div className="space-y-1">
              <label htmlFor={`${id}-kind`} className="block text-sm font-semibold">{t("kind")}</label>
              <select id={`${id}-kind`} value={d.kind} onChange={(e) => set({ kind: e.target.value as AssetContractKind })} className={fieldInput}>
                {contractKinds.map((k) => <option key={k} value={k}>{tKinds(k)}</option>)}
              </select>
            </div>
            <div className="min-w-0 flex-1 space-y-1">
              <label htmlFor={`${id}-name`} className="block text-sm font-semibold">{t("name")}</label>
              <input id={`${id}-name`} required maxLength={200} dir="auto" value={d.name} onChange={(e) => set({ name: e.target.value })} className={`${fieldInput} w-full`} />
            </div>
          </div>
          <div className="flex flex-wrap gap-4">
            <div className="space-y-1">
              <label htmlFor={`${id}-supplier`} className="block text-sm font-semibold">{t("supplier")}</label>
              <input id={`${id}-supplier`} maxLength={200} dir="auto" value={d.supplier} onChange={(e) => set({ supplier: e.target.value })} className={fieldInput} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-ref`} className="block text-sm font-semibold">{t("reference")}</label>
              <input id={`${id}-ref`} maxLength={100} dir="ltr" value={d.reference} onChange={(e) => set({ reference: e.target.value })} className={fieldInput} />
            </div>
          </div>
          <div className="flex flex-wrap gap-4">
            <div className="space-y-1">
              <label htmlFor={`${id}-starts`} className="block text-sm font-semibold">{t("startsOn")}</label>
              <input id={`${id}-starts`} type="date" dir="ltr" value={d.startsOn} onChange={(e) => set({ startsOn: e.target.value })} className={fieldInput} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-ends`} className="block text-sm font-semibold">{t("endsOn")}</label>
              <input id={`${id}-ends`} type="date" dir="ltr" required value={d.endsOn} onChange={(e) => set({ endsOn: e.target.value })} className={fieldInput} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-cost`} className="block text-sm font-semibold">{t("cost")}</label>
              <input id={`${id}-cost`} type="number" min={0} step="0.01" dir="ltr" value={d.costAed} onChange={(e) => set({ costAed: e.target.value })} className={`${fieldInput} max-w-40`} />
            </div>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-tags`} className="block text-sm font-semibold">{t("assetTags")}</label>
            <textarea id={`${id}-tags`} rows={2} dir="ltr" value={d.tags} aria-describedby={`${id}-tags-hint`} onChange={(e) => set({ tags: e.target.value.toUpperCase() })}
              className={`${fieldInput} w-full font-mono`} />
            <p id={`${id}-tags-hint`} className="text-sm text-ink-muted">{t("assetTagsHint")}</p>
          </div>
          {d.owner ? (
            <p>{t("owner", { name: d.owner.displayName ?? "" })} <button type="button" className="text-sm text-brand underline" onClick={() => set({ owner: null })}>{t("removeOwner")}</button></p>
          ) : <PersonSearch label={t("ownerSearch")} onPick={(owner) => set({ owner })} />}
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={primaryButton} disabled={busy || !d.name.trim() || !d.endsOn}>{t("save")}</button>
            <button type="button" className={secondaryButton} onClick={() => setEditing(null)}>{t("cancel")}</button>
          </div>
        </form>
      )}
    </div>
  );
}

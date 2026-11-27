"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { fieldButton, fieldInput } from "@/components/forms/ui";
import { cellClass, primaryButton, secondaryButton, tableClass } from "@/components/settings/integrations/ui";
import { ApiError, deleteRequest, getJson, patchJson, postJson } from "@/lib/client-api";
import type { LicenseMetric, SoftwareLicense, SoftwarePage, SoftwareProduct } from "@/lib/assets";
import { formatNumber, type DateDisplayOptions } from "@/lib/format";
import { WarrantyEnd } from "./AssetTable";

interface Draft {
  name: string;
  publisher: string;
  products: { key: string; name: string | null }[];
  metric: LicenseMetric;
  quantity: string;
  endsOn: string;
  supplier: string;
  reference: string;
}

const empty: Draft = { name: "", publisher: "", products: [], metric: "Device", quantity: "1", endsOn: "", supplier: "", reference: "" };

/**
 * Software licences and compliance (task G3): what discovery (G2) found installed against what is
 * owned, per licence: devices, people, used, shortfall (shown in words and colour) and spare.
 * A licence covers products chosen from the discovered software.
 */
export function LicensesPanel({ initial, canManage, csrfToken, display, warningDays }: { initial: SoftwareLicense[]; canManage: boolean; csrfToken: string; display: DateDisplayOptions; warningDays: number }) {
  const t = useTranslations("assets.licenses");
  const id = useId();
  const n = (value: number) => formatNumber(value, display.locale, display.digits);
  const [licenses, setLicenses] = useState(initial);
  const [editing, setEditing] = useState<{ license: SoftwareLicense | null; draft: Draft } | null>(null);
  const [query, setQuery] = useState("");
  const [products, setProducts] = useState<SoftwareProduct[] | null>(null);
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<Draft>) => editing && setEditing({ ...editing, draft: { ...editing.draft, ...patch } });

  async function search() {
    try {
      setProducts((await getJson<SoftwarePage>(`/v1/assets/software?${new URLSearchParams({ q: query.trim(), pageSize: "25" })}`)).items);
    } catch {
      setProducts([]);
    }
  }

  async function save() {
    if (!editing) return;
    const d = editing.draft;
    setBusy(true);
    setStatus(null);
    try {
      const body = {
        name: d.name.trim(), publisher: d.publisher, productKeys: d.products.map((p) => p.key), metric: d.metric, quantity: Number(d.quantity),
        endsOn: d.endsOn || "0001-01-01", supplier: d.supplier, reference: d.reference,
      };
      if (editing.license) await patchJson(`/v1/assets/licenses/${editing.license.id}`, body, csrfToken, { "If-Match": editing.license.etag });
      else await postJson("/v1/assets/licenses", body, csrfToken);
      setLicenses(await getJson<SoftwareLicense[]>("/v1/assets/licenses"));
      setEditing(null);
      setStatus({ ok: true, text: t("saved") });
    } catch (e) {
      setStatus({ ok: false, text: e instanceof ApiError && e.code && t.has(`errors.${e.code}`) ? t(`errors.${e.code}`) : t("errors.failed") });
    } finally {
      setBusy(false);
    }
  }

  async function remove(license: SoftwareLicense) {
    if (!window.confirm(t("confirmDelete", { name: license.name }))) return;
    try {
      await deleteRequest(`/v1/assets/licenses/${license.id}`, csrfToken);
      setLicenses((all) => all.filter((l) => l.id !== license.id));
      setStatus({ ok: true, text: t("deleted") });
    } catch {
      setStatus({ ok: false, text: t("errors.failed") });
    }
  }

  const d = editing?.draft;
  return (
    <div className="space-y-4" data-testid="licenses-panel">
      {licenses.length === 0 ? <p>{t("none")}</p> : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("title")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("name")}</th>
                <th scope="col" className={cellClass}>{t("owned")}</th>
                <th scope="col" className={cellClass}>{t("used")}</th>
                <th scope="col" className={cellClass}>{t("compliance")}</th>
                <th scope="col" className={cellClass}>{t("endsOn")}</th>
                {canManage && <th scope="col" className={cellClass}><span className="sr-only">{t("actions")}</span></th>}
              </tr>
            </thead>
            <tbody>
              {licenses.map((l) => (
                <tr key={l.id}>
                  <td className={cellClass}>
                    <span className="font-semibold" dir="auto">{l.name}</span>
                    <span className="block text-sm text-ink-muted">{t(l.metric === "User" ? "perUser" : "perDevice")}</span>
                  </td>
                  <td className={cellClass}>{n(l.quantity)}</td>
                  <td className={cellClass}>
                    {n(l.used)}
                    <span className="block text-xs text-ink-muted">{t("devicesPeople", { devices: n(l.devices), people: n(l.people) })}</span>
                  </td>
                  <td className={cellClass}>
                    {l.shortfall > 0
                      ? <span className="font-semibold text-bad">{t("shortfall", { count: l.shortfall, shown: n(l.shortfall) })}</span>
                      : <span className="text-ok">{t("spare", { count: l.spare, shown: n(l.spare) })}</span>}
                  </td>
                  <td className={cellClass}><WarrantyEnd endsOn={l.endsOn} warningDays={warningDays} display={display} /></td>
                  {canManage && (
                    <td className={cellClass}>
                      <div className="flex flex-wrap gap-2">
                        <button type="button" className={secondaryButton} onClick={() => setEditing({
                          license: l,
                          draft: { name: l.name, publisher: l.publisher ?? "", products: l.products, metric: l.metric, quantity: String(l.quantity), endsOn: l.endsOn ?? "", supplier: l.supplier ?? "", reference: l.reference ?? "" },
                        })}>{t("edit")}</button>
                        <button type="button" className={secondaryButton} onClick={() => void remove(l)}>{t("delete")}</button>
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
      {canManage && !editing && <button type="button" className={primaryButton} onClick={() => setEditing({ license: null, draft: empty })}>{t("add")}</button>}
      {editing && d && (
        <form className="max-w-2xl space-y-3 rounded-lg border border-line p-4" aria-labelledby={`${id}-form`} onSubmit={(e) => { e.preventDefault(); void save(); }}>
          <h2 id={`${id}-form`} className="text-lg font-semibold">{editing.license ? t("editHeading", { name: editing.license.name }) : t("addHeading")}</h2>
          <div className="space-y-1">
            <label htmlFor={`${id}-name`} className="block text-sm font-semibold">{t("name")}</label>
            <input id={`${id}-name`} required maxLength={200} dir="auto" value={d.name} onChange={(e) => set({ name: e.target.value })} className={`${fieldInput} w-full`} />
          </div>
          <fieldset className="space-y-2">
            <legend className="text-sm font-semibold">{t("products")}</legend>
            {d.products.length === 0 ? <p className="text-sm text-ink-muted">{t("noProducts")}</p> : (
              <ul className="space-y-1">
                {d.products.map((p) => (
                  <li key={p.key} className="flex flex-wrap items-center gap-2">
                    <span dir="auto">{p.name ?? p.key}</span>
                    <button type="button" className={fieldButton} onClick={() => set({ products: d.products.filter((x) => x.key !== p.key) })}>{t("removeProduct", { name: p.name ?? p.key })}</button>
                  </li>
                ))}
              </ul>
            )}
            <div className="flex flex-wrap gap-2">
              <label htmlFor={`${id}-q`} className="sr-only">{t("findProduct")}</label>
              <input id={`${id}-q`} type="search" dir="auto" placeholder={t("findProduct")} value={query} onChange={(e) => setQuery(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void search(); } }} className={`${fieldInput} max-w-xs`} />
              <button type="button" className={fieldButton} onClick={() => void search()}>{t("search")}</button>
            </div>
            <p role="status" aria-live="polite" className="text-sm">{products ? t("found", { count: products.length }) : ""}</p>
            {products && products.length > 0 && (
              <ul className="space-y-1">
                {products.filter((p) => !d.products.some((x) => x.key === p.productKey)).map((p) => (
                  <li key={p.productKey}>
                    <button type="button" className={`${fieldButton} text-start`} onClick={() => set({ products: [...d.products, { key: p.productKey, name: p.name }] })}>
                      <span dir="auto">{p.name}</span> {p.publisher && <span className="text-ink-muted" dir="auto">({p.publisher})</span>} · {t("onDevices", { count: p.devices, shown: n(p.devices) })}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </fieldset>
          <div className="flex flex-wrap gap-4">
            <fieldset>
              <legend className="text-sm font-semibold">{t("metric")}</legend>
              {(["Device", "User"] as const).map((m) => (
                <label key={m} className="flex items-center gap-2">
                  <input type="radio" name={`${id}-metric`} checked={d.metric === m} onChange={() => set({ metric: m })} className="size-5 accent-brand" />
                  {t(m === "User" ? "perUser" : "perDevice")}
                </label>
              ))}
            </fieldset>
            <div className="space-y-1">
              <label htmlFor={`${id}-qty`} className="block text-sm font-semibold">{t("owned")}</label>
              <input id={`${id}-qty`} type="number" min={0} max={10000000} dir="ltr" required value={d.quantity} onChange={(e) => set({ quantity: e.target.value })} className={`${fieldInput} max-w-32`} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-ends`} className="block text-sm font-semibold">{t("endsOn")}</label>
              <input id={`${id}-ends`} type="date" dir="ltr" value={d.endsOn} onChange={(e) => set({ endsOn: e.target.value })} className={fieldInput} />
            </div>
          </div>
          <div className="flex flex-wrap gap-4">
            <div className="space-y-1">
              <label htmlFor={`${id}-pub`} className="block text-sm font-semibold">{t("publisher")}</label>
              <input id={`${id}-pub`} maxLength={200} dir="auto" value={d.publisher} onChange={(e) => set({ publisher: e.target.value })} className={fieldInput} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-sup`} className="block text-sm font-semibold">{t("supplier")}</label>
              <input id={`${id}-sup`} maxLength={200} dir="auto" value={d.supplier} onChange={(e) => set({ supplier: e.target.value })} className={fieldInput} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-ref`} className="block text-sm font-semibold">{t("reference")}</label>
              <input id={`${id}-ref`} maxLength={100} dir="ltr" value={d.reference} onChange={(e) => set({ reference: e.target.value })} className={fieldInput} />
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={primaryButton} disabled={busy || !d.name.trim() || d.products.length === 0}>{t("save")}</button>
            <button type="button" className={secondaryButton} onClick={() => setEditing(null)}>{t("cancel")}</button>
          </div>
        </form>
      )}
    </div>
  );
}

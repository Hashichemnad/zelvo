"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { fieldInput } from "@/components/forms/ui";
import { primaryButton, secondaryButton } from "@/components/settings/integrations/ui";
import { ApiError, patchJson, postJson } from "@/lib/client-api";
import { formatDay, tagPattern, type Asset, type AssetPerson, type AssetState } from "@/lib/assets";
import { textIn } from "@/lib/cmdb";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { AssetStateBadge, PersonSearch } from "./common";
import { WarrantyEnd } from "./AssetTable";

/**
 * One asset (task G3): its tag, item, state and person, the moves its lifecycle allows next
 * (assigning names the person), purchase details, contracts and warranties, and the licences of
 * software found on it. Managers change it; readers see it.
 */
export function AssetDetail({ initial, canManage, csrfToken, display, warningDays }: { initial: Asset; canManage: boolean; csrfToken: string; display: DateDisplayOptions; warningDays: number }) {
  const t = useTranslations("assets.detail");
  const tStates = useTranslations("assets.states");
  const tKinds = useTranslations("assets.contractKinds");
  const locale = useLocale() as Locale;
  const id = useId();
  const [asset, setAsset] = useState(initial);
  const [next, setNext] = useState<AssetState | "">("");
  const [person, setPerson] = useState<AssetPerson | null>(null);
  const [note, setNote] = useState("");
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState({ tag: initial.tag, supplier: initial.supplier ?? "", orderNumber: initial.orderNumber ?? "", purchaseDate: initial.purchaseDate ?? "", purchaseCost: initial.purchaseCost?.toString() ?? "", notes: initial.notes ?? "" });
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const day = (iso: string | null) => (iso ? formatDay(iso, display.locale, display.calendar, display.digits) : "—");

  async function act(action: () => Promise<Asset>, done: string) {
    setBusy(true);
    setStatus(null);
    try {
      const saved = await action();
      setAsset(saved);
      setStatus({ ok: true, text: done });
      return true;
    } catch (e) {
      setStatus({ ok: false, text: e instanceof ApiError && e.code && t.has(`errors.${e.code}`) ? t(`errors.${e.code}`) : t("errors.failed") });
      return false;
    } finally {
      setBusy(false);
    }
  }

  const move = () =>
    act(() => postJson<Asset>(`/v1/assets/${asset.id}/state`, { state: next, assignedToId: next === "Assigned" ? person?.id : undefined, note: note.trim() || undefined }, csrfToken,
      { "If-Match": asset.etag }), t("moved")).then((ok) => {
      if (ok) {
        setNext("");
        setPerson(null);
        setNote("");
      }
    });

  const saveDetails = () =>
    act(() => patchJson<Asset>(`/v1/assets/${asset.id}`, {
      tag: draft.tag, supplier: draft.supplier, orderNumber: draft.orderNumber, notes: draft.notes,
      purchaseDate: draft.purchaseDate || "0001-01-01", purchaseCost: draft.purchaseCost === "" ? undefined : Number(draft.purchaseCost),
    }, csrfToken, { "If-Match": asset.etag }), t("saved")).then((ok) => ok && setEditing(false));

  const warranty = asset.contracts.filter((c) => c.kind === "Warranty").map((c) => c.endsOn).sort().at(-1) ?? null;

  return (
    <div className="space-y-6">
      <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{t("state")}</dt>
        <dd><AssetStateBadge state={asset.state} /></dd>
        <dt className="font-semibold">{t("item")}</dt>
        <dd>
          <Link href={`/cmdb/${asset.id}`} className="font-mono text-brand underline-offset-4 hover:underline" dir="ltr">{asset.number}</Link>{" "}
          <span className="text-sm text-ink-muted">{textIn(asset.class?.path, locale)}</span>
        </dd>
        <dt className="font-semibold">{t("assignedTo")}</dt>
        <dd>
          {asset.assignedTo ? <span dir="auto">{asset.assignedTo.displayName}</span> : "—"}
          {asset.assignedAt && <span className="block text-sm text-ink-muted">{t("since", { date: formatDateTime(new Date(asset.assignedAt), display) })}</span>}
        </dd>
        <dt className="font-semibold">{t("warranty")}</dt>
        <dd><WarrantyEnd endsOn={warranty} warningDays={warningDays} display={display} /></dd>
        <dt className="font-semibold">{t("request")}</dt>
        <dd>{asset.request ? <Link href={`/requests/${asset.request.id}`} className="text-brand underline-offset-4 hover:underline"><span dir="ltr" className="font-mono">{asset.request.number}</span> <span dir="auto">{asset.request.title}</span></Link> : "—"}</dd>
        <dt className="font-semibold">{t("purchased")}</dt>
        <dd>
          {day(asset.purchaseDate)}
          {asset.purchaseCost !== null && <span> · {t("cost", { amount: formatNumber(asset.purchaseCost, display.locale, display.digits) })}</span>}
          {asset.supplier && <span dir="auto"> · {asset.supplier}</span>}
          {asset.orderNumber && <span dir="ltr"> · {asset.orderNumber}</span>}
        </dd>
        {asset.notes && (<><dt className="font-semibold">{t("notes")}</dt><dd className="whitespace-pre-wrap" dir="auto">{asset.notes}</dd></>)}
      </dl>

      {canManage && asset.nextStates.length > 0 && (
        <form className="max-w-2xl space-y-3 rounded-lg border border-line p-4" aria-labelledby={`${id}-move`} onSubmit={(e) => { e.preventDefault(); void move(); }}>
          <h2 id={`${id}-move`} className="text-lg font-semibold">{t("moveHeading")}</h2>
          <fieldset className="space-y-1">
            <legend className="font-semibold">{t("nextState")}</legend>
            {asset.nextStates.map((s) => (
              <label key={s} className="flex items-center gap-2">
                <input type="radio" name={`${id}-next`} value={s} checked={next === s} onChange={() => setNext(s)} className="size-5 accent-brand" />
                {s === asset.state ? t("reassign") : tStates(s)}
              </label>
            ))}
          </fieldset>
          {next === "Assigned" && (person ? (
            <p>{t("assigningTo", { name: person.displayName ?? "" })} <button type="button" className="text-sm text-brand underline" onClick={() => setPerson(null)}>{t("otherPerson")}</button></p>
          ) : <PersonSearch label={t("person")} onPick={setPerson} />)}
          <div className="space-y-1">
            <label htmlFor={`${id}-note`} className="block text-sm font-semibold">{t("note")}</label>
            <input id={`${id}-note`} dir="auto" maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} className={`${fieldInput} max-w-md`} />
          </div>
          <button type="submit" className={primaryButton} disabled={busy || !next || (next === "Assigned" && !person)}>{t("move")}</button>
        </form>
      )}

      {canManage && (editing ? (
        <form className="max-w-2xl space-y-3 rounded-lg border border-line p-4" aria-labelledby={`${id}-edit`} onSubmit={(e) => { e.preventDefault(); void saveDetails(); }}>
          <h2 id={`${id}-edit`} className="text-lg font-semibold">{t("editHeading")}</h2>
          {([["tag", 32, "ltr"], ["supplier", 200, "auto"], ["orderNumber", 200, "ltr"]] as const).map(([key, max, dir]) => (
            <div key={key} className="space-y-1">
              <label htmlFor={`${id}-${key}`} className="block text-sm font-semibold">{t(`fields.${key}`)}</label>
              <input id={`${id}-${key}`} dir={dir} maxLength={max} value={draft[key]} onChange={(e) => setDraft({ ...draft, [key]: key === "tag" ? e.target.value.toUpperCase() : e.target.value })}
                aria-invalid={(key === "tag" && !tagPattern.test(draft.tag)) || undefined} className={`${fieldInput} max-w-md`} />
            </div>
          ))}
          <div className="flex flex-wrap gap-4">
            <div className="space-y-1">
              <label htmlFor={`${id}-date`} className="block text-sm font-semibold">{t("fields.purchaseDate")}</label>
              <input id={`${id}-date`} type="date" dir="ltr" value={draft.purchaseDate} onChange={(e) => setDraft({ ...draft, purchaseDate: e.target.value })} className={fieldInput} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-cost`} className="block text-sm font-semibold">{t("fields.purchaseCost")}</label>
              <input id={`${id}-cost`} type="number" min={0} step="0.01" dir="ltr" value={draft.purchaseCost} onChange={(e) => setDraft({ ...draft, purchaseCost: e.target.value })} className={`${fieldInput} max-w-40`} />
            </div>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-notes`} className="block text-sm font-semibold">{t("fields.notes")}</label>
            <textarea id={`${id}-notes`} dir="auto" rows={3} maxLength={2000} value={draft.notes} onChange={(e) => setDraft({ ...draft, notes: e.target.value })} className={`${fieldInput} w-full`} />
          </div>
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={primaryButton} disabled={busy || !tagPattern.test(draft.tag)}>{t("save")}</button>
            <button type="button" className={secondaryButton} onClick={() => setEditing(false)}>{t("cancel")}</button>
          </div>
        </form>
      ) : (
        <button type="button" className={secondaryButton} onClick={() => setEditing(true)}>{t("edit")}</button>
      ))}
      <p role="status" aria-live="polite" className={status?.ok === false ? "font-semibold text-bad" : "text-ok"}>{status?.text ?? ""}</p>

      <section aria-labelledby={`${id}-contracts`} className="space-y-2">
        <h2 id={`${id}-contracts`} className="text-xl font-bold">{t("contractsHeading")}</h2>
        {asset.contracts.length === 0 ? <p>{t("noContracts")}</p> : (
          <ul className="space-y-1">
            {asset.contracts.map((c) => (
              <li key={c.id}>
                <span className="font-semibold" dir="auto">{c.name}</span> · {tKinds(c.kind)}{c.supplier && <span dir="auto"> · {c.supplier}</span>} · {t("endsOn", { date: day(c.endsOn) })}
              </li>
            ))}
          </ul>
        )}
        <Link href="/assets/contracts" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("allContracts")}</Link>
      </section>
      <section aria-labelledby={`${id}-licenses`} className="space-y-2">
        <h2 id={`${id}-licenses`} className="text-xl font-bold">{t("licensesHeading")}</h2>
        {asset.licenses.length === 0 ? <p>{t("noLicenses")}</p> : (
          <ul className="list-disc space-y-1 ps-5">{asset.licenses.map((l) => <li key={l.id} dir="auto">{l.name}</li>)}</ul>
        )}
      </section>
    </div>
  );
}

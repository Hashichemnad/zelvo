"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, cellClass, primaryButton, secondaryButton, smallButton, tableClass } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { deleteRequest, getJson, patchJson, postJson } from "@/lib/client-api";
import { impactDirections, textIn, type CiRelationshipType, type ImpactDirection } from "@/lib/cmdb";
import { toInput } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { CmdbStatusLine, useSaver } from "./common";

interface Draft {
  key: string;
  outgoing: { en: string; ar: string };
  incoming: { en: string; ar: string };
  impact: ImpactDirection;
}

const empty: Draft = { key: "", outgoing: { en: "", ar: "" }, incoming: { en: "", ar: "" }, impact: "ToSource" };
const keyPattern = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/**
 * Settings → CMDB → Relationship types (task G1): the four built in (runs on, depends on,
 * connects to, used by) and the organisation's own, each with a bilingual name for both directions
 * and which way faults travel (fixed for built-in types).
 */
export function RelationshipTypesEditor({ initial, csrfToken, canManage }: { initial: CiRelationshipType[]; csrfToken: string; canManage: boolean }) {
  const t = useTranslations("cmdb.types");
  const tImpact = useTranslations("cmdb.impactDirections");
  const locale = useLocale() as Locale;
  const id = useId();
  const [types, setTypes] = useState(initial);
  const [editing, setEditing] = useState<CiRelationshipType | "new" | null>(null);
  const [draft, setDraft] = useState<Draft>(empty);
  const { status, run, busy } = useSaver();

  const reload = async () => setTypes(await getJson<CiRelationshipType[]>("/v1/cmdb/relationship-types"));
  const change = (action: () => Promise<unknown>) =>
    run(async () => {
      await action();
      await reload();
      return true;
    });

  function start(next: CiRelationshipType | "new" | null) {
    setEditing(next);
    setDraft(next && next !== "new"
      ? { key: next.key, outgoing: { en: next.outgoingName.en ?? "", ar: next.outgoingName.ar ?? "" }, incoming: { en: next.incomingName.en ?? "", ar: next.incomingName.ar ?? "" }, impact: next.impact }
      : empty);
  }

  const builtIn = editing !== null && editing !== "new" && editing.builtIn;
  const keyInvalid = editing === "new" && draft.key !== "" && !keyPattern.test(draft.key);
  const form = editing && (
    <form className="space-y-3 rounded-lg border border-line p-4" aria-labelledby={`${id}-heading`}
      onSubmit={(e) => {
        e.preventDefault();
        const body = { outgoingName: toInput(draft.outgoing.en, draft.outgoing.ar), incomingName: toInput(draft.incoming.en, draft.incoming.ar), ...(builtIn ? {} : { impact: draft.impact }) };
        void change(() => (editing === "new"
          ? postJson("/v1/cmdb/relationship-types", { ...body, key: draft.key }, csrfToken)
          : patchJson(`/v1/cmdb/relationship-types/${editing.id}`, body, csrfToken))).then((ok) => ok && start(null));
      }}>
      <h4 id={`${id}-heading`} className="text-lg font-semibold">
        {editing === "new" ? t("addHeading") : t("editHeading", { name: textIn(editing.outgoingName, locale) })}
      </h4>
      {editing === "new" && (
        <div className="space-y-1">
          <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
          <input id={`${id}-key`} dir="ltr" required maxLength={50} value={draft.key} aria-describedby={`${id}-key-hint`} aria-invalid={keyInvalid || undefined}
            onChange={(e) => setDraft({ ...draft, key: e.target.value.toLowerCase() })} className={`${fieldInput} max-w-xs`} />
          <p id={`${id}-key-hint`} className={`text-sm ${keyInvalid ? "font-semibold text-bad" : "text-ink-muted"}`}>{t("keyHint")}</p>
        </div>
      )}
      <BilingualInputs id={`${id}-out`} label={t("outgoing")} en={draft.outgoing.en} ar={draft.outgoing.ar} describedBy={`${id}-out-hint`}
        onChange={(outgoing) => setDraft({ ...draft, outgoing })} />
      <p id={`${id}-out-hint`} className="text-sm text-ink-muted">{t("outgoingHint")}</p>
      <BilingualInputs id={`${id}-in`} label={t("incoming")} en={draft.incoming.en} ar={draft.incoming.ar} describedBy={`${id}-in-hint`}
        onChange={(incoming) => setDraft({ ...draft, incoming })} />
      <p id={`${id}-in-hint`} className="text-sm text-ink-muted">{t("incomingHint")}</p>
      <div className="space-y-1">
        <label htmlFor={`${id}-impact`} className="block font-semibold">{t("impact")}</label>
        <select id={`${id}-impact`} value={draft.impact} disabled={builtIn} aria-describedby={`${id}-impact-hint`}
          onChange={(e) => setDraft({ ...draft, impact: e.target.value as ImpactDirection })} className={`${fieldInput} max-w-md`}>
          {impactDirections.map((d) => <option key={d} value={d}>{tImpact(d)}</option>)}
        </select>
        <p id={`${id}-impact-hint`} className="text-sm text-ink-muted">{builtIn ? t("impactBuiltIn") : t("impactHint")}</p>
      </div>
      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryButton} disabled={busy || keyInvalid || (editing === "new" && !draft.key)
          || (!draft.outgoing.en.trim() && !draft.outgoing.ar.trim()) || (!draft.incoming.en.trim() && !draft.incoming.ar.trim())}>
          {t("save")}
        </button>
        <button type="button" className={secondaryButton} onClick={() => start(null)}>{t("cancel")}</button>
      </div>
    </form>
  );

  return (
    <div className="space-y-4" data-testid="cmdb-types">
      <div className="overflow-x-auto">
        <table className={tableClass}>
          <caption className="sr-only">{t("caption")}</caption>
          <thead>
            <tr>
              <th scope="col" className={cellClass}>{t("outgoing")}</th>
              <th scope="col" className={cellClass}>{t("incoming")}</th>
              <th scope="col" className={cellClass}>{t("impact")}</th>
              <th scope="col" className={cellClass}>{t("relationships")}</th>
              {canManage && <th scope="col" className={cellClass}><span className="sr-only">{t("actions")}</span></th>}
            </tr>
          </thead>
          <tbody>
            {types.map((type) => {
              const name = textIn(type.outgoingName, locale) || type.key;
              return (
                <tr key={type.id} data-testid="cmdb-type">
                  <td className={cellClass}>
                    <span className={type.active ? "font-semibold" : "font-semibold text-ink-muted line-through"}>{name}</span>
                    {type.builtIn && <span className="ms-2 rounded-full border border-line px-2 text-xs">{t("builtIn")}</span>}
                    {!type.active && <span className="ms-2 rounded-full border border-line px-2 text-xs">{t("inactive")}</span>}
                    {type.targetKind === "Consumer" && <span className="block text-xs text-ink-muted">{t("consumerTarget")}</span>}
                  </td>
                  <td className={cellClass}>{textIn(type.incomingName, locale)}</td>
                  <td className={cellClass}>{tImpact(type.impact)}</td>
                  <td className={cellClass}>{type.relationshipCount}</td>
                  {canManage && (
                    <td className={cellClass}>
                      <span className="flex flex-wrap gap-1">
                        <button type="button" className={smallButton} disabled={busy} onClick={() => start(type)}>{t("edit", { name })}</button>
                        <button type="button" className={smallButton} disabled={busy}
                          onClick={() => void change(() => patchJson(`/v1/cmdb/relationship-types/${type.id}`, { active: !type.active }, csrfToken))}>
                          {type.active ? t("deactivate", { name }) : t("activate", { name })}
                        </button>
                        {!type.builtIn && (
                          <button type="button" className={smallButton} disabled={busy}
                            onClick={() => void change(() => deleteRequest(`/v1/cmdb/relationship-types/${type.id}`, csrfToken))}>
                            {t("delete", { name })}
                          </button>
                        )}
                      </span>
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {form}
      {canManage && editing === null && (
        <button type="button" className={primaryButton} onClick={() => start("new")}>{t("add")}</button>
      )}
      <CmdbStatusLine status={status} />
    </div>
  );
}

"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, primaryButton, secondaryButton, smallButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { ApiError, deleteRequest, getJson, patchJson, postJson } from "@/lib/client-api";
import { ciFamilies, classesByFamily, maxClassDepth, textIn, type CiClass, type CiFamily } from "@/lib/cmdb";
import { toInput } from "@/lib/directory";
import type { FormField, FormProblem } from "@/lib/forms/types";
import type { Locale } from "@/lib/incidents";
import { AttributesEditor } from "./AttributesEditor";
import { CmdbStatusLine, FamilyLabel, useSaver } from "./common";

type Editing = { mode: "add"; parent: CiClass | null; family: CiFamily } | { mode: "edit"; ciClass: CiClass } | null;

interface Draft {
  key: string;
  family: CiFamily;
  name: { en: string; ar: string };
  description: { en: string; ar: string };
  requesterVisible: boolean;
  attributes: FormField[];
}

const keyPattern = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

function draftOf(editing: NonNullable<Editing>): Draft {
  if (editing.mode === "edit") {
    const c = editing.ciClass;
    return {
      key: c.key,
      family: c.family,
      name: { en: c.name.en ?? "", ar: c.name.ar ?? "" },
      description: { en: c.description.en ?? "", ar: c.description.ar ?? "" },
      requesterVisible: c.requesterVisible,
      attributes: c.attributes ?? [],
    };
  }
  return { key: "", family: editing.parent?.family ?? editing.family, name: { en: "", ar: "" }, description: { en: "", ar: "" }, requesterVisible: false, attributes: [] };
}

/**
 * Settings → CMDB → Classes (task G1): the class tree under each family (hardware, software,
 * business services, applications, network, cloud resources), up to four levels, with each
 * class's own attributes and those it inherits. Administrators add, rename, move, activate and
 * delete classes, choose whether requesters may pick their items, and add Zelvo's standard classes.
 */
export function ClassesEditor({ initial, csrfToken, canManage }: { initial: CiClass[]; csrfToken: string; canManage: boolean }) {
  const t = useTranslations("cmdb.classes");
  const tFamilies = useTranslations("cmdb.families");
  const locale = useLocale() as Locale;
  const id = useId();
  const [classes, setClasses] = useState(initial);
  const [editing, setEditing] = useState<Editing>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [problems, setProblems] = useState<FormProblem[]>([]);
  const { status, run, busy } = useSaver();

  const reload = async () => setClasses(await getJson<CiClass[]>("/v1/cmdb/classes"));
  const change = (action: () => Promise<unknown>) =>
    run(async () => {
      await action();
      await reload();
    });

  function start(next: Editing) {
    setEditing(next);
    setDraft(next ? draftOf(next) : null);
    setProblems([]);
  }

  const siblingsOf = (c: CiClass) => classes.filter((x) => x.parentId === c.parentId && (c.parentId !== null || x.family === c.family));
  const nameOf = (c: CiClass) => textIn(c.name, locale) || c.key;

  async function save() {
    if (!editing || !draft) return;
    setProblems([]);
    const body = {
      name: toInput(draft.name.en, draft.name.ar),
      description: { en: draft.description.en.trim() || null, ar: draft.description.ar.trim() || null },
      requesterVisible: draft.requesterVisible,
      attributes: draft.attributes,
    };
    const result = await run(async () => {
      try {
        if (editing.mode === "add") {
          await postJson("/v1/cmdb/classes", { ...body, key: draft.key, parentId: editing.parent?.id ?? null, family: editing.parent ? null : draft.family }, csrfToken);
        } else {
          await patchJson(`/v1/cmdb/classes/${editing.ciClass.id}`, body, csrfToken, { "If-Match": editing.ciClass.etag });
        }
      } catch (error) {
        if (error instanceof ApiError && Array.isArray(error.details.problems)) setProblems(error.details.problems as FormProblem[]);
        throw error;
      }
      await reload();
      return true;
    });
    if (result) start(null);
  }

  const keyInvalid = editing?.mode === "add" && draft !== null && draft.key !== "" && !keyPattern.test(draft.key);
  const form = editing && draft && (
    <form className="space-y-4 rounded-lg border border-line p-4" aria-labelledby={`${id}-form-heading`}
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}>
      <h4 id={`${id}-form-heading`} className="text-lg font-semibold">
        {editing.mode === "edit"
          ? t("editHeading", { name: nameOf(editing.ciClass) })
          : editing.parent
            ? t("addChildHeading", { name: nameOf(editing.parent) })
            : t("addHeading")}
      </h4>
      {editing.mode === "add" && (
        <div className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1">
            <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
            <input id={`${id}-key`} dir="ltr" value={draft.key} maxLength={50} required aria-describedby={`${id}-key-hint`} aria-invalid={keyInvalid || undefined}
              onChange={(e) => setDraft({ ...draft, key: e.target.value.toLowerCase() })} className={fieldInput} />
            <p id={`${id}-key-hint`} className={`text-sm ${keyInvalid ? "font-semibold text-bad" : "text-ink-muted"}`}>{t("keyHint")}</p>
          </div>
          {!editing.parent && (
            <div className="space-y-1">
              <label htmlFor={`${id}-family`} className="block font-semibold">{t("family")}</label>
              <select id={`${id}-family`} value={draft.family} onChange={(e) => setDraft({ ...draft, family: e.target.value as CiFamily })} className={fieldInput}>
                {ciFamilies.map((f) => <option key={f} value={f}>{tFamilies(f)}</option>)}
              </select>
            </div>
          )}
        </div>
      )}
      <BilingualInputs id={`${id}-name`} label={t("name")} maxLength={150} en={draft.name.en} ar={draft.name.ar} onChange={(name) => setDraft({ ...draft, name })} />
      <BilingualInputs id={`${id}-description`} label={t("description")} maxLength={1000} en={draft.description.en} ar={draft.description.ar}
        onChange={(description) => setDraft({ ...draft, description })} />
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={draft.requesterVisible} aria-describedby={`${id}-visible-hint`} className="size-6 shrink-0 accent-brand"
          onChange={(e) => setDraft({ ...draft, requesterVisible: e.target.checked })} />
        {t("requesterVisible")}
      </label>
      <p id={`${id}-visible-hint`} className="ms-8 text-sm text-ink-muted">{t("requesterVisibleHint")}</p>
      <AttributesEditor attributes={draft.attributes} problems={problems} onChange={(attributes) => setDraft({ ...draft, attributes })}
        inherited={editing.mode === "edit" ? editing.ciClass.inheritedAttributes : inheritedFor(editing.parent)} />
      {problems.some((p) => !draft.attributes.some((a) => a.key === p.field)) && (
        <p role="alert" className="font-semibold text-bad">{t("problemsOther")}</p>
      )}
      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryButton} disabled={busy || keyInvalid || (editing.mode === "add" && !draft.key) || (!draft.name.en.trim() && !draft.name.ar.trim())}>
          {t("save")}
        </button>
        <button type="button" className={secondaryButton} onClick={() => start(null)}>{t("cancel")}</button>
      </div>
    </form>
  );

  function inheritedFor(parent: CiClass | null) {
    if (!parent) return [];
    return [...parent.inheritedAttributes, ...(parent.attributes ?? []).map((field) => ({ classId: parent.id, className: parent.name, field }))];
  }

  return (
    <div className="space-y-4" data-testid="cmdb-classes">
      {canManage && (
        <div className="flex flex-wrap gap-2">
          <button type="button" className={primaryButton} disabled={busy} onClick={() => start({ mode: "add", parent: null, family: "Hardware" })}>
            {t("add")}
          </button>
          <button type="button" className={secondaryButton} disabled={busy} aria-describedby={`${id}-standard-hint`}
            onClick={() => void change(() => postJson("/v1/cmdb/classes/standard", undefined, csrfToken))}>
            {t("addStandard")}
          </button>
          <p id={`${id}-standard-hint`} className="w-full text-sm text-ink-muted">{t("addStandardHint")}</p>
        </div>
      )}
      {editing?.mode === "add" && !editing.parent && form}
      {classes.length === 0 && <p>{t("empty")}</p>}
      {classesByFamily(classes).map(({ family, classes: members }) => (
        <section key={family} aria-labelledby={`${id}-${family}`} className="space-y-2">
          <h4 id={`${id}-${family}`} className="text-lg font-semibold"><FamilyLabel family={family} /></h4>
          <ul className="space-y-2">
            {members.map((c) => {
              const siblings = siblingsOf(c);
              const index = siblings.findIndex((s) => s.id === c.id);
              const name = nameOf(c);
              return (
                <li key={c.id} data-testid="cmdb-class" style={{ marginInlineStart: `${(c.depth - 1) * 1.5}rem` }}>
                  <div className="flex flex-wrap items-center gap-2 rounded-md bg-surface p-2">
                    <span className={`font-semibold ${c.active ? "" : "text-ink-muted line-through"}`}>{name}</span>
                    <span className="font-mono text-xs text-ink-muted" dir="ltr">{c.key}</span>
                    <span className="text-sm text-ink-muted">{t("counts", { items: c.itemCount, attributes: (c.attributes ?? []).length })}</span>
                    {!c.active && <span className="rounded-full border border-line px-2 text-xs">{t("inactive")}</span>}
                    {c.requesterVisible && <span className="rounded-full border border-line px-2 text-xs">{t("forRequesters")}</span>}
                    {canManage && (
                      <span className="ms-auto flex flex-wrap gap-1">
                        <button type="button" className={smallButton} disabled={busy} onClick={() => start({ mode: "edit", ciClass: c })}>
                          {t("edit", { name })}
                        </button>
                        {c.depth < maxClassDepth && (
                          <button type="button" className={smallButton} disabled={busy} onClick={() => start({ mode: "add", parent: c, family: c.family })}>
                            {t("addChild", { name })}
                          </button>
                        )}
                        <button type="button" className={smallButton} disabled={busy || index <= 0}
                          onClick={() => void change(() => patchJson(`/v1/cmdb/classes/${c.id}`, { position: index - 1 }, csrfToken, { "If-Match": c.etag }))}>
                          {t("moveUp", { name })}
                        </button>
                        <button type="button" className={smallButton} disabled={busy || index === siblings.length - 1}
                          onClick={() => void change(() => patchJson(`/v1/cmdb/classes/${c.id}`, { position: index + 1 }, csrfToken, { "If-Match": c.etag }))}>
                          {t("moveDown", { name })}
                        </button>
                        <button type="button" className={smallButton} disabled={busy}
                          onClick={() => void change(() => patchJson(`/v1/cmdb/classes/${c.id}`, { active: !c.active }, csrfToken, { "If-Match": c.etag }))}>
                          {c.active ? t("deactivate", { name }) : t("activate", { name })}
                        </button>
                        <button type="button" className={smallButton} disabled={busy}
                          onClick={() => void change(() => deleteRequest(`/v1/cmdb/classes/${c.id}`, csrfToken))}>
                          {t("delete", { name })}
                        </button>
                      </span>
                    )}
                  </div>
                  {editing?.mode === "edit" && editing.ciClass.id === c.id && <div className="mt-2">{form}</div>}
                  {editing?.mode === "add" && editing.parent?.id === c.id && <div className="mt-2">{form}</div>}
                </li>
              );
            })}
          </ul>
        </section>
      ))}
      <CmdbStatusLine status={status} />
    </div>
  );
}

"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { SaveStatusLine, smallButton, useSaver } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { buttonClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { patchJson, postJson } from "@/lib/client-api";
import { inLanguage, type Locale } from "@/lib/incidents";
import {
  maxStates, prefixPattern, recordStateCategories, statesProblem, typeKeyPattern, type RecordStateCategory, type RecordType,
} from "@/lib/records";

interface StateDraft {
  key: string;
  name: { en: string; ar: string };
  category: RecordStateCategory;
  inUse: boolean;
}

interface Draft {
  key: string;
  nameEn: string;
  nameAr: string;
  descriptionEn: string;
  descriptionAr: string;
  numberPrefix: string;
  portalEntry: boolean;
  defaultPriority: number;
  active: boolean;
  states: StateDraft[];
}

const starterStates: StateDraft[] = [
  { key: "new", name: { en: "New", ar: "جديد" }, category: "New", inUse: false },
  { key: "in-progress", name: { en: "In progress", ar: "قيد التنفيذ" }, category: "InProgress", inUse: false },
  { key: "on-hold", name: { en: "On hold", ar: "معلّق" }, category: "OnHold", inUse: false },
  { key: "done", name: { en: "Done", ar: "منجز" }, category: "Resolved", inUse: false },
  { key: "closed", name: { en: "Closed", ar: "مغلق" }, category: "Closed", inUse: false },
];

function draftOf(type: RecordType | null): Draft {
  return type
    ? {
        key: type.key, nameEn: type.name.en ?? "", nameAr: type.name.ar ?? "", descriptionEn: type.description.en ?? "", descriptionAr: type.description.ar ?? "",
        numberPrefix: type.numberPrefix, portalEntry: type.portalEntry, defaultPriority: type.defaultPriority, active: type.active,
        states: type.states.map((s) => ({ key: s.key, name: { en: s.name.en ?? "", ar: s.name.ar ?? "" }, category: s.category, inUse: s.inUse })),
      }
    : { key: "", nameEn: "", nameAr: "", descriptionEn: "", descriptionAr: "", numberPrefix: "", portalEntry: true, defaultPriority: 3, active: true, states: starterStates };
}

/**
 * Settings → Record types (task K2): the tenant's custom record types and the editor of one. Each
 * state is mapped onto a category of the ticket core (New, In progress, On hold, Resolved, Closed),
 * so SLAs, reports and auto-close work; states in use keep their category and cannot be removed.
 * New types grant their permissions to agents, team leads and administrators.
 */
export function RecordTypesEditor({ initial, csrfToken }: { initial: RecordType[]; csrfToken: string }) {
  const t = useTranslations("records.settings");
  const tCategories = useTranslations("records.categories");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [editing, setEditing] = useState<RecordType | "new" | null>(null);
  const [draft, setDraft] = useState<Draft>(draftOf(null));
  const existing = editing !== "new" && editing !== null ? editing : null;
  const problem = statesProblem(draft.states);
  const valid = (existing || typeKeyPattern.test(draft.key)) && prefixPattern.test(draft.numberPrefix) && (draft.nameEn.trim() || draft.nameAr.trim()) && !problem;

  function open(type: RecordType | "new") {
    setEditing(type);
    setDraft(draftOf(type === "new" ? null : type));
  }

  function setState(i: number, change: Partial<StateDraft>) {
    setDraft({ ...draft, states: draft.states.map((s, j) => (j === i ? { ...s, ...change } : s)) });
  }

  function moveState(i: number, by: number) {
    const states = [...draft.states];
    [states[i], states[i + by]] = [states[i + by]!, states[i]!];
    setDraft({ ...draft, states });
  }

  function save() {
    const body = {
      name: { en: draft.nameEn.trim() || null, ar: draft.nameAr.trim() || null },
      description: { en: draft.descriptionEn.trim() || null, ar: draft.descriptionAr.trim() || null },
      numberPrefix: draft.numberPrefix,
      portalEntry: draft.portalEntry,
      defaultPriority: draft.defaultPriority,
      states: draft.states.map((s) => ({ key: s.key, name: { en: s.name.en.trim() || null, ar: s.name.ar.trim() || null }, category: s.category })),
    };
    void run(async () => {
      if (existing) await patchJson(`/v1/record-types/${existing.key}`, { ...body, active: draft.active }, csrfToken, { "If-Match": existing.etag });
      else await postJson("/v1/record-types", { ...body, key: draft.key }, csrfToken);
      setEditing(null);
      router.refresh();
    });
  }

  return (
    <div className="space-y-6">
      {initial.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-2" data-testid="record-types">
          {initial.map((type) => (
            <li key={type.id} className="flex flex-wrap items-center gap-3 rounded-md border border-line p-3">
              <span className="font-semibold">{inLanguage(type.name, locale).value}</span>
              <span className="font-mono text-sm text-ink-muted" dir="ltr">{type.numberPrefix}-000000</span>
              {!type.active && <span className="text-sm text-ink-muted">{t("inactive")}</span>}
              {type.portalEntry && <span className="text-sm">{t("inPortal")}</span>}
              <button type="button" className={smallButton} onClick={() => open(type)}>{t("edit", { name: inLanguage(type.name, locale).value })}</button>
            </li>
          ))}
        </ul>
      )}
      {editing === null && <button type="button" className={buttonClass} onClick={() => open("new")}>{t("new")}</button>}
      {editing !== null && (
        <form className="space-y-4 rounded-lg border border-line p-4" aria-labelledby={`${id}-heading`} onSubmit={(e) => (e.preventDefault(), valid && save())}>
          <h3 id={`${id}-heading`} className="text-lg font-semibold">{existing ? t("editing", { name: inLanguage(existing.name, locale).value }) : t("new")}</h3>
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="space-y-1">
              <span className="block font-semibold">{t("key")}</span>
              <input dir="ltr" value={draft.key} maxLength={40} readOnly={!!existing} aria-describedby={`${id}-key-hint`}
                onChange={(e) => setDraft({ ...draft, key: e.target.value.toLowerCase() })} className={fieldInput} />
              <span id={`${id}-key-hint`} className="block text-sm text-ink-muted">{t("keyHint")}</span>
            </label>
            <label className="space-y-1">
              <span className="block font-semibold">{t("prefix")}</span>
              <input dir="ltr" value={draft.numberPrefix} maxLength={10} onChange={(e) => setDraft({ ...draft, numberPrefix: e.target.value.toUpperCase() })} className={fieldInput} />
            </label>
            <label className="space-y-1">
              <span className="block font-semibold">{t("nameEn")}</span>
              <input lang="en" dir="ltr" value={draft.nameEn} maxLength={100} onChange={(e) => setDraft({ ...draft, nameEn: e.target.value })} className={fieldInput} />
            </label>
            <label className="space-y-1">
              <span className="block font-semibold">{t("nameAr")}</span>
              <input lang="ar" dir="rtl" value={draft.nameAr} maxLength={100} onChange={(e) => setDraft({ ...draft, nameAr: e.target.value })} className={fieldInput} />
            </label>
            <label className="space-y-1">
              <span className="block font-semibold">{t("descriptionEn")}</span>
              <textarea lang="en" dir="ltr" value={draft.descriptionEn} maxLength={1000} rows={2} onChange={(e) => setDraft({ ...draft, descriptionEn: e.target.value })} className={fieldInput} />
            </label>
            <label className="space-y-1">
              <span className="block font-semibold">{t("descriptionAr")}</span>
              <textarea lang="ar" dir="rtl" value={draft.descriptionAr} maxLength={1000} rows={2} onChange={(e) => setDraft({ ...draft, descriptionAr: e.target.value })} className={fieldInput} />
            </label>
            <label className="space-y-1">
              <span className="block font-semibold">{t("priority")}</span>
              <select value={draft.defaultPriority} onChange={(e) => setDraft({ ...draft, defaultPriority: Number(e.target.value) })} className={fieldInput}>
                {[1, 2, 3, 4, 5].map((p) => <option key={p} value={p}>{`P${p}`}</option>)}
              </select>
            </label>
            <div className="space-y-2">
              <label className="flex items-center gap-2">
                <input type="checkbox" checked={draft.portalEntry} onChange={(e) => setDraft({ ...draft, portalEntry: e.target.checked })} className="size-5 accent-brand" />
                {t("portalEntry")}
              </label>
              {existing && (
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={draft.active} onChange={(e) => setDraft({ ...draft, active: e.target.checked })} className="size-5 accent-brand" />
                  {t("active")}
                </label>
              )}
            </div>
          </div>
          <fieldset className="space-y-2">
            <legend className="font-semibold">{t("states")}</legend>
            <p className="text-sm text-ink-muted">{t("statesHint")}</p>
            <ol className="space-y-2">
              {draft.states.map((s, i) => (
                <li key={i} className="grid gap-2 rounded-md border border-line p-2 sm:grid-cols-4 sm:items-end">
                  <label className="space-y-1 text-sm">
                    <span className="block">{t("stateKey", { n: i + 1 })}</span>
                    <input dir="ltr" value={s.key} maxLength={40} readOnly={s.inUse} onChange={(e) => setState(i, { key: e.target.value.toLowerCase() })} className={fieldInput} />
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="block">{t("stateNameEn", { n: i + 1 })}</span>
                    <input lang="en" dir="ltr" value={s.name.en} maxLength={100} onChange={(e) => setState(i, { name: { ...s.name, en: e.target.value } })} className={fieldInput} />
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="block">{t("stateNameAr", { n: i + 1 })}</span>
                    <input lang="ar" dir="rtl" value={s.name.ar} maxLength={100} onChange={(e) => setState(i, { name: { ...s.name, ar: e.target.value } })} className={fieldInput} />
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="block">{t("stateCategory", { n: i + 1 })}</span>
                    <select value={s.category} disabled={s.inUse} onChange={(e) => setState(i, { category: e.target.value as RecordStateCategory })} className={fieldInput}>
                      {recordStateCategories.map((c) => <option key={c} value={c}>{tCategories(c)}</option>)}
                    </select>
                  </label>
                  <div className="flex flex-wrap gap-2 sm:col-span-4">
                    <button type="button" className={smallButton} disabled={i === 0} onClick={() => moveState(i, -1)}>{t("moveUp", { key: s.key || i + 1 })}</button>
                    <button type="button" className={smallButton} disabled={i === draft.states.length - 1} onClick={() => moveState(i, 1)}>{t("moveDown", { key: s.key || i + 1 })}</button>
                    {!s.inUse && (
                      <button type="button" className={smallButton} onClick={() => setDraft({ ...draft, states: draft.states.filter((_, j) => j !== i) })}>
                        {t("removeState", { key: s.key || i + 1 })}
                      </button>
                    )}
                    {s.inUse && <span className="text-sm text-ink-muted">{t("inUse")}</span>}
                  </div>
                </li>
              ))}
            </ol>
            {draft.states.length < maxStates && (
              <button type="button" className={smallButton}
                onClick={() => setDraft({ ...draft, states: [...draft.states, { key: "", name: { en: "", ar: "" }, category: "InProgress", inUse: false }] })}>
                {t("addState")}
              </button>
            )}
            {problem && <p className="text-sm font-semibold text-bad">{t(`problems.${problem}`)}</p>}
          </fieldset>
          {!existing && <p className="text-sm text-ink-muted">{t("grantsHint")}</p>}
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={buttonClass} disabled={busy || !valid}>{t("save")}</button>
            <button type="button" className={secondaryButtonClass} onClick={() => setEditing(null)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      <SaveStatusLine status={status} />
    </div>
  );
}

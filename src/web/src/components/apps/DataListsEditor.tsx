"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, SaveStatusLine, smallButton, useSaver } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { buttonClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { columnKeyPattern, dataListKeyPattern, type DataList } from "@/lib/apps";
import { postJson, putJson } from "@/lib/client-api";
import { inLanguage, type Locale } from "@/lib/incidents";

interface ItemDraft {
  key: string;
  en: string;
  ar: string;
  values: Record<string, string>;
  active: boolean;
}

interface Draft {
  key: string;
  name: { en: string; ar: string };
  description: { en: string; ar: string };
  columns: { key: string; en: string; ar: string }[];
  items: ItemDraft[];
}

const maxItems = 1000;
const maxColumns = 10;

function draftOf(list: DataList | null): Draft {
  return list
    ? {
        key: list.key,
        name: { en: list.name.en ?? "", ar: list.name.ar ?? "" },
        description: { en: list.description.en ?? "", ar: list.description.ar ?? "" },
        columns: list.columns.map((c) => ({ key: c.key, en: c.label.en ?? "", ar: c.label.ar ?? "" })),
        items: list.items.map((i) => ({ key: i.key, en: i.label.en ?? "", ar: i.label.ar ?? "", values: { ...i.values }, active: i.active })),
      }
    : { key: "", name: { en: "", ar: "" }, description: { en: "", ar: "" }, columns: [], items: [{ key: "item1", en: "", ar: "", values: {}, active: true }] };
}

const text = (en: string, ar: string) => ({ en: en.trim() || null, ar: ar.trim() || null });

/** Why a draft cannot be saved, as a message key, or null. */
export function dataListProblem(draft: Draft, isNew: boolean): string | null {
  if (isNew && !dataListKeyPattern.test(draft.key)) return "key";
  if (!draft.name.en.trim() && !draft.name.ar.trim()) return "name";
  const columnKeys = draft.columns.map((c) => c.key);
  if (draft.columns.some((c) => !columnKeyPattern.test(c.key) || (!c.en.trim() && !c.ar.trim())) || new Set(columnKeys).size !== columnKeys.length) return "columns";
  const itemKeys = draft.items.map((i) => i.key.trim());
  if (itemKeys.some((k) => k.length === 0 || k.length > 100) || new Set(itemKeys).size !== itemKeys.length) return "itemKeys";
  if (draft.items.some((i) => !i.en.trim() && !i.ar.trim())) return "itemLabels";
  return null;
}

/**
 * Settings → Data lists (task K3): administrator-defined tables, for example vehicles or rooms,
 * that dropdown and radio fields of forms use as their options. Items left out are no longer
 * offered; answers that chose them keep them.
 */
export function DataListsEditor({ initial, csrfToken }: { initial: DataList[]; csrfToken: string }) {
  const t = useTranslations("apps.dataLists");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [editing, setEditing] = useState<DataList | "new" | null>(null);
  const [draft, setDraft] = useState<Draft>(draftOf(null));
  const existing = editing !== "new" && editing !== null ? editing : null;
  const problem = dataListProblem(draft, !existing);

  function open(list: DataList | "new") {
    setEditing(list);
    setDraft(draftOf(list === "new" ? null : list));
  }

  function setItem(i: number, change: Partial<ItemDraft>) {
    setDraft({ ...draft, items: draft.items.map((item, j) => (j === i ? { ...item, ...change } : item)) });
  }

  function moveItem(i: number, by: number) {
    const items = [...draft.items];
    [items[i], items[i + by]] = [items[i + by]!, items[i]!];
    setDraft({ ...draft, items });
  }

  function save() {
    const columnKeys = new Set(draft.columns.map((c) => c.key));
    const body = {
      name: text(draft.name.en, draft.name.ar),
      description: text(draft.description.en, draft.description.ar),
      columns: draft.columns.map((c) => ({ key: c.key, label: text(c.en, c.ar) })),
      items: draft.items.map((i) => ({
        key: i.key.trim(),
        label: text(i.en, i.ar),
        values: Object.fromEntries(Object.entries(i.values).filter(([k, v]) => columnKeys.has(k) && v.trim() !== "").map(([k, v]) => [k, v.trim()])),
        active: i.active,
      })),
    };
    void run(async () => {
      if (existing) await putJson(`/v1/data-lists/${existing.key}`, body, csrfToken, { "If-Match": existing.etag });
      else await postJson("/v1/data-lists", { ...body, key: draft.key }, csrfToken);
      setEditing(null);
      router.refresh();
    });
  }

  return (
    <div className="space-y-6">
      {initial.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-2" data-testid="data-lists">
          {initial.map((list) => (
            <li key={list.id} className="flex flex-wrap items-center gap-3 rounded-md border border-line p-3">
              <span className="font-semibold">{inLanguage(list.name, locale).value}</span>
              <span className="font-mono text-sm text-ink-muted" dir="ltr">{list.key}</span>
              <span className="text-sm">{t("itemCount", { count: list.items.filter((i) => i.active).length })}</span>
              {list.app && <span className="text-sm text-ink-muted">{t("inApp", { app: list.app })}</span>}
              <button type="button" className={smallButton} onClick={() => open(list)}>{t("edit", { name: inLanguage(list.name, locale).value })}</button>
            </li>
          ))}
        </ul>
      )}
      {editing === null && <button type="button" className={buttonClass} onClick={() => open("new")}>{t("new")}</button>}
      {editing !== null && (
        <form className="min-w-0 space-y-4 rounded-lg border border-line p-4" aria-labelledby={`${id}-heading`} onSubmit={(e) => (e.preventDefault(), !problem && save())}>
          <h3 id={`${id}-heading`} className="text-lg font-semibold">{existing ? t("editing", { name: inLanguage(existing.name, locale).value }) : t("new")}</h3>
          <div className="max-w-sm space-y-1">
            <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
            <input id={`${id}-key`} dir="ltr" value={draft.key} maxLength={40} readOnly={!!existing} aria-describedby={`${id}-key-hint`}
              onChange={(e) => setDraft({ ...draft, key: e.target.value })} className={fieldInput} />
            <span id={`${id}-key-hint`} className="block text-sm text-ink-muted">{t("keyHint")}</span>
          </div>
          <BilingualInputs id={`${id}-name`} label={t("name")} en={draft.name.en} ar={draft.name.ar} onChange={(name) => setDraft({ ...draft, name })} />
          <BilingualInputs id={`${id}-description`} label={t("description")} maxLength={1000} en={draft.description.en} ar={draft.description.ar}
            onChange={(description) => setDraft({ ...draft, description })} />

          <fieldset className="space-y-2">
            <legend className="font-semibold">{t("columns")}</legend>
            <p className="text-sm text-ink-muted">{t("columnsHint")}</p>
            {draft.columns.map((c, i) => (
              <div key={i} className="grid gap-2 rounded-md border border-line p-2 sm:grid-cols-4 sm:items-end">
                <label className="space-y-1 text-sm">
                  <span className="block">{t("columnKey", { n: i + 1 })}</span>
                  <input dir="ltr" value={c.key} maxLength={20} className={fieldInput}
                    onChange={(e) => setDraft({ ...draft, columns: draft.columns.map((x, j) => (j === i ? { ...x, key: e.target.value.replace(/[^A-Za-z0-9]/g, "") } : x)) })} />
                </label>
                <label className="space-y-1 text-sm">
                  <span className="block">{t("columnEn", { n: i + 1 })}</span>
                  <input lang="en" dir="ltr" value={c.en} maxLength={100} className={fieldInput}
                    onChange={(e) => setDraft({ ...draft, columns: draft.columns.map((x, j) => (j === i ? { ...x, en: e.target.value } : x)) })} />
                </label>
                <label className="space-y-1 text-sm">
                  <span className="block">{t("columnAr", { n: i + 1 })}</span>
                  <input lang="ar" dir="rtl" value={c.ar} maxLength={100} className={fieldInput}
                    onChange={(e) => setDraft({ ...draft, columns: draft.columns.map((x, j) => (j === i ? { ...x, ar: e.target.value } : x)) })} />
                </label>
                <button type="button" className={smallButton} onClick={() => setDraft({ ...draft, columns: draft.columns.filter((_, j) => j !== i) })}>
                  {t("removeColumn", { n: i + 1 })}
                </button>
              </div>
            ))}
            {draft.columns.length < maxColumns && (
              <button type="button" className={smallButton} onClick={() => setDraft({ ...draft, columns: [...draft.columns, { key: `column${draft.columns.length + 1}`, en: "", ar: "" }] })}>
                {t("addColumn")}
              </button>
            )}
          </fieldset>

          <fieldset className="space-y-2">
            <legend className="font-semibold">{t("items")}</legend>
            <p className="text-sm text-ink-muted">{t("itemsHint")}</p>
            <ol className="space-y-2">
              {draft.items.map((item, i) => (
                <li key={i} className="grid gap-2 rounded-md border border-line p-2 sm:grid-cols-3 sm:items-end">
                  <label className="space-y-1 text-sm">
                    <span className="block">{t("itemKey", { n: i + 1 })}</span>
                    <input dir="ltr" value={item.key} maxLength={100} className={fieldInput} onChange={(e) => setItem(i, { key: e.target.value })} />
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="block">{t("itemEn", { n: i + 1 })}</span>
                    <input lang="en" dir="ltr" value={item.en} maxLength={200} className={fieldInput} onChange={(e) => setItem(i, { en: e.target.value })} />
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="block">{t("itemAr", { n: i + 1 })}</span>
                    <input lang="ar" dir="rtl" value={item.ar} maxLength={200} className={fieldInput} onChange={(e) => setItem(i, { ar: e.target.value })} />
                  </label>
                  {draft.columns.map((c) => (
                    <label key={c.key} className="space-y-1 text-sm">
                      <span className="block">{t("itemValue", { n: i + 1, column: (locale === "ar" ? c.ar || c.en : c.en || c.ar) || c.key })}</span>
                      <input dir="auto" value={item.values[c.key] ?? ""} maxLength={500} className={fieldInput}
                        onChange={(e) => setItem(i, { values: { ...item.values, [c.key]: e.target.value } })} />
                    </label>
                  ))}
                  <div className="flex flex-wrap items-center gap-2 sm:col-span-3">
                    <label className="flex items-center gap-2 text-sm">
                      <input type="checkbox" checked={item.active} onChange={(e) => setItem(i, { active: e.target.checked })} className="size-5 accent-brand" />
                      {t("itemActive", { n: i + 1 })}
                    </label>
                    <button type="button" className={smallButton} disabled={i === 0} onClick={() => moveItem(i, -1)}>{t("moveUp", { n: i + 1 })}</button>
                    <button type="button" className={smallButton} disabled={i === draft.items.length - 1} onClick={() => moveItem(i, 1)}>{t("moveDown", { n: i + 1 })}</button>
                    <button type="button" className={smallButton} onClick={() => setDraft({ ...draft, items: draft.items.filter((_, j) => j !== i) })}>{t("removeItem", { n: i + 1 })}</button>
                  </div>
                </li>
              ))}
            </ol>
            {draft.items.length < maxItems && (
              <button type="button" className={smallButton}
                onClick={() => setDraft({ ...draft, items: [...draft.items, { key: `item${draft.items.length + 1}`, en: "", ar: "", values: {}, active: true }] })}>
                {t("addItem")}
              </button>
            )}
          </fieldset>
          {problem && <p className="text-sm font-semibold text-bad">{t(`problems.${problem}`)}</p>}
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={buttonClass} disabled={busy || !!problem}>{t("save")}</button>
            <button type="button" className={secondaryButtonClass} onClick={() => setEditing(null)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      <SaveStatusLine status={status} />
    </div>
  );
}

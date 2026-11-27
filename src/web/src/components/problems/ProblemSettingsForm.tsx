"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { primaryButton, smallButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { putJson } from "@/lib/client-api";
import type { FishboneCategory, ProblemSettings } from "@/lib/problems";
import { ProblemStatusLine, useSaver } from "./common";

const prefixPattern = /^[A-Z][A-Z0-9]{0,9}$/;
const keyPattern = /^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$/;

/** Settings → Problems (task H1): the known error number prefix and the fishbone template's categories, in order, with move buttons. */
export function ProblemSettingsForm({ initial, csrfToken }: { initial: ProblemSettings; csrfToken: string }) {
  const t = useTranslations("problems.settings");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [prefix, setPrefix] = useState(initial.knownErrorPrefix);
  const [categories, setCategories] = useState<FishboneCategory[]>(initial.fishboneCategories);
  const valid = prefixPattern.test(prefix) && categories.length > 0 && categories.length <= 12 && categories.some((c) => c.active)
    && categories.every((c) => keyPattern.test(c.key) && (c.name.en?.trim() || c.name.ar?.trim()))
    && new Set(categories.map((c) => c.key)).size === categories.length;
  const update = (i: number, change: Partial<FishboneCategory>) => setCategories(categories.map((c, j) => (j === i ? { ...c, ...change } : c)));
  const move = (i: number, by: number) => {
    const next = [...categories];
    [next[i], next[i + by]] = [next[i + by]!, next[i]!];
    setCategories(next);
  };

  return (
    <form className="space-y-4" aria-labelledby={`${id}-heading`} onSubmit={(e) => {
      e.preventDefault();
      if (valid) void run(() => putJson("/v1/settings/problems", { knownErrorPrefix: prefix, fishboneCategories: categories }, csrfToken));
    }}>
      <h3 id={`${id}-heading`} className="sr-only">{t("title")}</h3>
      <label className="block space-y-1">
        <span className="block font-semibold">{t("prefix")}</span>
        <input dir="ltr" value={prefix} maxLength={10} onChange={(e) => setPrefix(e.target.value.toUpperCase())} className={`${fieldInput} max-w-40`} />
      </label>
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("categories")}</legend>
        <ol className="space-y-2">
          {categories.map((c, i) => (
            <li key={i} className="grid gap-2 rounded-md border border-line p-2 sm:grid-cols-4 sm:items-end">
              <label className="space-y-1 text-sm">
                <span className="block">{t("key")}</span>
                <input dir="ltr" value={c.key} maxLength={50} onChange={(e) => update(i, { key: e.target.value })} className={fieldInput} />
              </label>
              <label className="space-y-1 text-sm">
                <span className="block">{t("nameEn")}</span>
                <input lang="en" dir="ltr" value={c.name.en ?? ""} maxLength={100} onChange={(e) => update(i, { name: { ...c.name, en: e.target.value } })} className={fieldInput} />
              </label>
              <label className="space-y-1 text-sm">
                <span className="block">{t("nameAr")}</span>
                <input lang="ar" dir="rtl" value={c.name.ar ?? ""} maxLength={100} onChange={(e) => update(i, { name: { ...c.name, ar: e.target.value } })} className={fieldInput} />
              </label>
              <div className="flex flex-wrap items-center gap-2">
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={c.active} onChange={(e) => update(i, { active: e.target.checked })} className="size-5 accent-brand" />
                  {t("active")}
                </label>
                <button type="button" className={smallButton} disabled={i === 0} onClick={() => move(i, -1)}>{t("moveUp", { key: c.key })}</button>
                <button type="button" className={smallButton} disabled={i === categories.length - 1} onClick={() => move(i, 1)}>{t("moveDown", { key: c.key })}</button>
                <button type="button" className={smallButton} onClick={() => setCategories(categories.filter((_, j) => j !== i))}>{t("remove", { key: c.key })}</button>
              </div>
            </li>
          ))}
        </ol>
        {categories.length < 12 && (
          <button type="button" className={smallButton} onClick={() => setCategories([...categories, { key: "", name: { en: "", ar: "" }, active: true }])}>{t("add")}</button>
        )}
      </fieldset>
      <button type="submit" className={primaryButton} disabled={busy || !valid}>{t("save")}</button>
      <ProblemStatusLine status={status} />
    </form>
  );
}

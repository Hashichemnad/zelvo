"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldInput } from "@/components/forms/ui";
import { getJson } from "@/lib/client-api";
import type { DataList } from "@/lib/apps";
import { inLanguage, type Locale } from "@/lib/incidents";

/**
 * Task K3: where a dropdown or radio field's options come from: its own options, or a data list
 * (Settings → Data lists), whose active items become the options when the form is filled in.
 */
export function DataListSource({ value, onChange }: { value: string | null; onChange: (key: string | null) => void }) {
  const t = useTranslations("apps.source");
  const locale = useLocale() as Locale;
  const id = useId();
  const [lists, setLists] = useState<DataList[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let live = true;
    getJson<DataList[]>("/v1/data-lists")
      .then((found) => live && setLists(Array.isArray(found) ? found : []))
      .catch(() => live && setFailed(true));
    return () => {
      live = false;
    };
  }, []);

  const chosen = lists?.find((l) => l.key === value);
  return (
    <div className="space-y-1">
      <label htmlFor={`${id}-source`} className="block font-semibold">{t("label")}</label>
      <select id={`${id}-source`} value={value ?? ""} aria-describedby={`${id}-hint`} onChange={(e) => onChange(e.target.value || null)} className={fieldInput}>
        <option value="">{t("own")}</option>
        {value && !chosen && <option value={value}>{value}</option>}
        {(lists ?? []).map((l) => <option key={l.key} value={l.key}>{inLanguage(l.name, locale).value}</option>)}
      </select>
      <p id={`${id}-hint`} className="text-sm text-ink-muted">
        {chosen ? t("fromList", { count: chosen.items.filter((i) => i.active).length }) : t("hint")}
      </p>
      {failed && <p role="alert" className="text-sm">{t("unavailable")}</p>}
    </div>
  );
}

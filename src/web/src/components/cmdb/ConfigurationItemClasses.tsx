"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { getJson } from "@/lib/client-api";
import { textIn, type CiClass } from "@/lib/cmdb";
import type { Locale } from "@/lib/incidents";

/**
 * The form builder's choice of classes for a configuration item field (task G1): items of the
 * chosen classes and the classes below them can be picked; none chosen means any class.
 */
export function ConfigurationItemClasses({ chosen, onChange }: { chosen: string[]; onChange: (classes: string[]) => void }) {
  const t = useTranslations("cmdb.picker");
  const locale = useLocale() as Locale;
  const id = useId();
  const [classes, setClasses] = useState<CiClass[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let live = true;
    getJson<CiClass[]>("/v1/cmdb/classes")
      .then((list) => live && setClasses(list))
      .catch(() => live && setFailed(true));
    return () => {
      live = false;
    };
  }, []);

  return (
    <fieldset className="space-y-2" aria-describedby={`${id}-hint`}>
      <legend className="font-semibold">{t("classes")}</legend>
      <p id={`${id}-hint`} className="text-sm text-ink-muted">{t("classesHint")}</p>
      {failed && <p role="alert" className="text-sm">{t("classesUnavailable")}</p>}
      {classes && classes.length === 0 && <p className="text-sm">{t("noClasses")}</p>}
      {classes && classes.length > 0 && (
        <ul className="max-h-64 space-y-1 overflow-y-auto">
          {classes.map((c) => (
            <li key={c.id} style={{ paddingInlineStart: `${(c.depth - 1) * 1.25}rem` }}>
              <label className="flex items-center gap-2">
                <input type="checkbox" checked={chosen.includes(c.id)} className="size-5 shrink-0 accent-brand"
                  onChange={(e) => onChange(e.target.checked ? [...chosen, c.id] : chosen.filter((x) => x !== c.id))} />
                <span>{textIn(c.name, locale) || c.key}</span>
                {!c.active && <span className="text-xs text-ink-muted">({t("inactive")})</span>}
              </label>
            </li>
          ))}
        </ul>
      )}
    </fieldset>
  );
}

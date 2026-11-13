"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { putJson } from "@/lib/client-api";
import type { ApiCalendar, ApiDigits } from "@/lib/format";

export interface LocalizationValues {
  defaultLanguage: "en" | "ar";
  defaultCalendar: ApiCalendar;
  defaultDigits: ApiDigits;
}

const languages = ["en", "ar"] as const;
const calendars: ApiCalendar[] = ["Gregorian", "Hijri"];
const digitSystems: ApiDigits[] = ["Western", "ArabicIndic"];

const selectClass =
  "w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

/** Settings → Language and region: the tenant's default language (LANG-1), calendar and digits (LANG-8). */
export function LocalizationSettingsForm({ initial, csrfToken }: { initial: LocalizationValues; csrfToken: string }) {
  const t = useTranslations("settings.language");
  const tDisplay = useTranslations("display");
  const [values, setValues] = useState(initial);
  const [status, setStatus] = useState<"idle" | "saving" | "saved" | "failed">("idle");
  const id = useId();

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setStatus("saving");
    try {
      setValues(await putJson<LocalizationValues>("/v1/settings/localization", values, csrfToken));
      setStatus("saved");
    } catch {
      setStatus("failed");
    }
  }

  const change = <K extends keyof LocalizationValues>(key: K) => (event: React.ChangeEvent<HTMLSelectElement>) => {
    setValues({ ...values, [key]: event.target.value as LocalizationValues[K] });
    setStatus("idle");
  };

  return (
    <form onSubmit={save} className="max-w-xl space-y-5" data-testid="localization-settings">
      <div className="space-y-1">
        <label htmlFor={`${id}-language`} className="block font-semibold">
          {t("defaultLanguage")}
        </label>
        <select id={`${id}-language`} name="defaultLanguage" value={values.defaultLanguage} onChange={change("defaultLanguage")} className={selectClass}>
          {languages.map((language) => (
            <option key={language} value={language}>
              {tDisplay(`languages.${language}`)}
            </option>
          ))}
        </select>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-calendar`} className="block font-semibold">
          {t("defaultCalendar")}
        </label>
        <select id={`${id}-calendar`} name="defaultCalendar" value={values.defaultCalendar} onChange={change("defaultCalendar")} className={selectClass}>
          {calendars.map((calendar) => (
            <option key={calendar} value={calendar}>
              {tDisplay(`calendars.${calendar}`)}
            </option>
          ))}
        </select>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-digits`} className="block font-semibold">
          {t("defaultDigits")}
        </label>
        <select id={`${id}-digits`} name="defaultDigits" value={values.defaultDigits} onChange={change("defaultDigits")} className={selectClass}>
          {digitSystems.map((digits) => (
            <option key={digits} value={digits}>
              {tDisplay(`digits.${digits}`)}
            </option>
          ))}
        </select>
      </div>
      <div className="flex flex-wrap items-center gap-4">
        <button
          type="submit"
          disabled={status === "saving"}
          className="rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60"
        >
          {status === "saving" ? t("saving") : t("save")}
        </button>
        <p role="status" aria-live="polite" className={status === "failed" ? "text-bad" : "text-ok"}>
          {status === "saved" ? t("saved") : status === "failed" ? t("failed") : ""}
        </p>
      </div>
    </form>
  );
}

"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { usePathname, useRouter } from "@/i18n/navigation";
import type { Preferences } from "@/lib/api";
import { putJson } from "@/lib/client-api";
import { formatLongDate, formatNumber, toCalendarSystem, toDigitSystem, type ApiCalendar, type ApiDigits } from "@/lib/format";

export interface PreferencesFormProps {
  initial: Preferences;
  /** The tenant defaults, shown as "Organisation default (…)". */
  defaults: { language: "en" | "ar"; calendar: ApiCalendar; digits: ApiDigits };
  csrfToken: string;
}

type Choice<T extends string> = T | "default";

const selectClass =
  "w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

/**
 * The user's own language (LANG-2), calendar and digits (LANG-8), each either a choice or the
 * organisation default, with a preview. Saving a new language shows this page in it.
 */
export function PreferencesForm({ initial, defaults, csrfToken }: PreferencesFormProps) {
  const t = useTranslations("preferences");
  const tDisplay = useTranslations("display");
  const locale = useLocale();
  const router = useRouter();
  const pathname = usePathname();
  const id = useId();
  const [language, setLanguage] = useState<Choice<"en" | "ar">>(initial.language ?? "default");
  const [calendar, setCalendar] = useState<Choice<ApiCalendar>>(initial.calendar ?? "default");
  const [digits, setDigits] = useState<Choice<ApiDigits>>(initial.digits ?? "default");
  const [status, setStatus] = useState<"idle" | "saving" | "saved" | "failed">("idle");

  const effectiveCalendar = calendar === "default" ? defaults.calendar : calendar;
  const effectiveDigits = digits === "default" ? defaults.digits : digits;
  const [sampleDate] = useState(() => new Date());

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setStatus("saving");
    try {
      const saved = await putJson<Preferences>(
        "/v1/me/preferences",
        {
          language: language === "default" ? null : language,
          calendar: calendar === "default" ? null : calendar,
          digits: digits === "default" ? null : digits,
        },
        csrfToken,
      );
      setStatus("saved");
      if (saved.effective.language !== locale) {
        router.replace(pathname, { locale: saved.effective.language });
      } else {
        router.refresh();
      }
    } catch {
      setStatus("failed");
    }
  }

  function changed<T>(set: (value: T) => void) {
    return (event: React.ChangeEvent<HTMLSelectElement>) => {
      set(event.target.value as T);
      setStatus("idle");
    };
  }

  return (
    <form onSubmit={save} className="space-y-5" data-testid="preferences-form">
      <div className="grid gap-4 sm:grid-cols-3">
        <div className="space-y-1">
          <label htmlFor={`${id}-language`} className="block font-semibold">
            {t("language")}
          </label>
          <select id={`${id}-language`} name="language" value={language} onChange={changed(setLanguage)} className={selectClass}>
            <option value="default">{t("useDefault", { value: tDisplay(`languages.${defaults.language}`) })}</option>
            <option value="en">{tDisplay("languages.en")}</option>
            <option value="ar">{tDisplay("languages.ar")}</option>
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-calendar`} className="block font-semibold">
            {t("calendar")}
          </label>
          <select id={`${id}-calendar`} name="calendar" value={calendar} onChange={changed(setCalendar)} className={selectClass}>
            <option value="default">{t("useDefault", { value: tDisplay(`calendars.${defaults.calendar}`) })}</option>
            <option value="Gregorian">{tDisplay("calendars.Gregorian")}</option>
            <option value="Hijri">{tDisplay("calendars.Hijri")}</option>
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-digits`} className="block font-semibold">
            {t("digits")}
          </label>
          <select id={`${id}-digits`} name="digits" value={digits} onChange={changed(setDigits)} className={selectClass}>
            <option value="default">{t("useDefault", { value: tDisplay(`digits.${defaults.digits}`) })}</option>
            <option value="Western">{tDisplay("digits.Western")}</option>
            <option value="ArabicIndic">{tDisplay("digits.ArabicIndic")}</option>
          </select>
        </div>
      </div>

      <section aria-labelledby={`${id}-preview`} className="rounded-lg border border-line bg-surface-muted p-4">
        <h3 id={`${id}-preview`} className="mb-2 font-semibold">
          {t("preview")}
        </h3>
        <dl className="grid gap-2 sm:grid-cols-2">
          <div>
            <dt className="text-sm text-ink-muted">{t("previewDate")}</dt>
            <dd data-testid="preview-date">
              {formatLongDate(sampleDate, {
                locale,
                calendar: toCalendarSystem(effectiveCalendar),
                digits: toDigitSystem(effectiveDigits),
                timeZone: "Asia/Dubai",
              })}
            </dd>
          </div>
          <div>
            <dt className="text-sm text-ink-muted">{t("previewNumber")}</dt>
            <dd data-testid="preview-number">{formatNumber(1234567.89, locale, toDigitSystem(effectiveDigits))}</dd>
          </div>
        </dl>
      </section>

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

/**
 * Date and number display (LANG-8): Gregorian or Hijri calendar, Western or Arabic-Indic digits.
 * Per-user choices arrive with the profile settings; callers pass them in explicitly.
 */

export type CalendarSystem = "gregory" | "islamic-umalqura";
export type DigitSystem = "latn" | "arab";

export interface DateDisplayOptions {
  locale: string;
  calendar: CalendarSystem;
  digits: DigitSystem;
  timeZone: string;
}

/** BCP 47 locale for display, e.g. "ar-AE" for Arabic and "en-AE" for English. */
export function displayLocale(locale: string): string {
  return `${locale}-AE`;
}

export function formatLongDate(date: Date, options: DateDisplayOptions): string {
  return new Intl.DateTimeFormat(displayLocale(options.locale), {
    calendar: options.calendar,
    numberingSystem: options.digits,
    timeZone: options.timeZone,
    day: "numeric",
    month: "long",
    year: "numeric",
    weekday: "long",
  }).format(date);
}

export function formatNumber(value: number, locale: string, digits: DigitSystem): string {
  return new Intl.NumberFormat(displayLocale(locale), { numberingSystem: digits }).format(value);
}

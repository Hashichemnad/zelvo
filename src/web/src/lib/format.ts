/**
 * Date and number display (LANG-8): Gregorian or Hijri calendar, Western or Arabic-Indic digits.
 * The tenant sets the defaults and each user can override them; `getDisplayFormat` (server) gives
 * what applies, and callers pass it in explicitly.
 */

export type CalendarSystem = "gregory" | "islamic-umalqura";
export type DigitSystem = "latn" | "arab";

/** Calendar and digit choices as the API names them. */
export type ApiCalendar = "Gregorian" | "Hijri";
export type ApiDigits = "Western" | "ArabicIndic";

export function toCalendarSystem(calendar: ApiCalendar): CalendarSystem {
  return calendar === "Hijri" ? "islamic-umalqura" : "gregory";
}

export function toDigitSystem(digits: ApiDigits): DigitSystem {
  return digits === "ArabicIndic" ? "arab" : "latn";
}

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

/** Date and time, for example "13 November 2026 at 11:18 PM", in the user's calendar and digits. */
export function formatDateTime(date: Date, options: DateDisplayOptions): string {
  return new Intl.DateTimeFormat(displayLocale(options.locale), {
    calendar: options.calendar,
    numberingSystem: options.digits,
    timeZone: options.timeZone,
    dateStyle: "long",
    timeStyle: "short",
  }).format(date);
}

import "server-only";
import { getCurrentUser, getLocalizationSettings, type DisplaySettings } from "./api";
import { getAuthConfig } from "./auth/config";
import { getSession } from "./auth/session";
import { toCalendarSystem, toDigitSystem, type CalendarSystem, type DigitSystem } from "./format";

export interface DisplayFormat {
  calendar: CalendarSystem;
  digits: DigitSystem;
  timeZone: string;
}

const fallback: Omit<DisplaySettings, "language"> = { calendar: "Gregorian", digits: "Western" };

/**
 * Calendar and digits for this request (LANG-8): the signed-in user's choices over the tenant
 * defaults, or the tenant defaults for visitors; Gregorian and Western digits if neither can be read.
 */
export async function getDisplayFormat(): Promise<DisplayFormat> {
  const config = getAuthConfig();
  const session = await getSession(config).catch(() => null);
  let settings: Omit<DisplaySettings, "language"> | null = null;
  if (session) {
    settings = (await getCurrentUser(session.data.accessToken, config.tenant))?.preferences.effective ?? null;
  }
  if (!settings) {
    const tenant = await getLocalizationSettings(config.tenant);
    settings = tenant ? { calendar: tenant.defaultCalendar, digits: tenant.defaultDigits } : fallback;
  }
  return {
    calendar: toCalendarSystem(settings.calendar),
    digits: toDigitSystem(settings.digits),
    // UAE time until the tenant time-zone setting exists.
    timeZone: "Asia/Dubai",
  };
}

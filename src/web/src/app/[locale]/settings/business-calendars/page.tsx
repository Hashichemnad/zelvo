import { getTranslations, setRequestLocale } from "next-intl/server";
import { CalendarsPanel } from "@/components/sla/CalendarsPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { BusinessCalendar, CalendarTemplate } from "@/lib/sla";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/business-calendars">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.businessCalendars" });
  return { title: t("title") };
}

/** Settings → Business calendars (module 8, GOV-7; task B3): working hours, time zone and public holidays. */
export default async function BusinessCalendarsPage({ params }: PageProps<"/[locale]/settings/business-calendars">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/business-calendars`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "sla.calendars" }),
    getTranslations({ locale, namespace: "settings.sections.businessCalendars" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("directory.read") ?? false;
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [calendars, templates, format] = await Promise.all([
    allowed ? get<BusinessCalendar[]>("/api/v1/sla/calendars") : Promise.resolve(null),
    allowed ? get<CalendarTemplate[]>("/api/v1/sla/calendar-templates") : Promise.resolve(null),
    getDisplayFormat(),
  ]);

  return (
    <section aria-labelledby="calendars-heading" className="space-y-4">
      <h2 id="calendars-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!calendars || !templates ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <CalendarsPanel initial={calendars} templates={templates} csrfToken={session.data.csrfToken}
            canManage={user!.permissions.includes("sla.manage")} display={{ locale, ...format }} />
        </>
      )}
    </section>
  );
}

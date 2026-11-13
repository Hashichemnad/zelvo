import { Suspense } from "react";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { SystemStatus } from "@/components/SystemStatus";
import { getDirection, otherLocale, type Locale } from "@/i18n/routing";
import { getDisplayFormat } from "@/lib/display";
import { formatLongDate, type CalendarSystem } from "@/lib/format";

export default async function WelcomePage({ params }: PageProps<"/[locale]">) {
  const { locale: rawLocale } = await params;
  const locale = rawLocale as Locale;
  setRequestLocale(locale);

  const other = otherLocale(locale);
  const [t, tStatus, tOther, display] = await Promise.all([
    getTranslations({ locale, namespace: "welcome" }),
    getTranslations({ locale, namespace: "status" }),
    getTranslations({ locale: other, namespace: "welcome" }),
    getDisplayFormat(),
  ]);

  const now = new Date();
  const dateOptions = { locale, digits: display.digits, timeZone: display.timeZone };
  // The calendar that applies (LANG-8) first, then the other one.
  const calendars: { calendar: CalendarSystem; name: string }[] = [
    { calendar: "gregory", name: t("gregorian") },
    { calendar: "islamic-umalqura", name: t("hijri") },
  ].sort((a, b) => Number(b.calendar === display.calendar) - Number(a.calendar === display.calendar)) as { calendar: CalendarSystem; name: string }[];

  return (
    <div className="space-y-10">
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand md:text-4xl">{t("greeting")}</h1>
        <p lang={other} dir={getDirection(other)} className="text-xl text-ink-muted">
          {tOther("otherLanguageGreeting")}
        </p>
        <p className="max-w-2xl text-lg">{t("subtitle")}</p>
        <p className="max-w-2xl rounded-md border-s-4 border-brand bg-surface-muted px-4 py-3 text-sm">
          {t("setupNotice")}
        </p>
      </section>

      <section aria-labelledby="today-heading">
        <h2 id="today-heading" className="mb-3 text-xl font-bold">
          {t("todayHeading")}
        </h2>
        <dl className="grid gap-3 sm:grid-cols-2">
          {calendars.map(({ calendar, name }, index) => (
            <div key={calendar} className="rounded-lg border border-line bg-surface p-4" data-testid={index === 0 ? "today-primary" : "today-secondary"}>
              <dt className="text-sm text-ink-muted">
                {name} · {index === 0 ? t("yourCalendar") : t("otherCalendar")}
              </dt>
              <dd className="font-semibold">{formatLongDate(now, { ...dateOptions, calendar })}</dd>
            </div>
          ))}
        </dl>
      </section>

      <Suspense fallback={<p role="status">{tStatus("checking")}</p>}>
        <SystemStatus />
      </Suspense>
    </div>
  );
}

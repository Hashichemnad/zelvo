import { getTranslations, setRequestLocale } from "next-intl/server";
import { PortalSearch } from "@/components/portal/PortalSearch";
import { TicketProgressList } from "@/components/portal/TicketProgress";
import { PortalHappiness } from "@/components/voice/VoiceViews";
import { Link } from "@/i18n/navigation";
import { getDirection, otherLocale, type Locale } from "@/i18n/routing";
import { getJsonAs } from "@/lib/api";
import type { CatalogBrowse } from "@/lib/catalog";
import { iconGlyph } from "@/lib/catalog";
import { getDisplayFormat } from "@/lib/display";
import { textOf } from "@/lib/directory";
import { formatLongDate, type CalendarSystem } from "@/lib/format";
import type { PortalTicketPage } from "@/lib/portal";
import { loadVoiceSettings, optionalSession } from "@/lib/voice-pages";

/**
 * The self-service portal's home (module 4, task D2): "How can we help?" searching the catalog,
 * the knowledge base and the person's own tickets; raising a ticket in one click (report a
 * problem, request a service, contact us); the person's open tickets with their progress; popular
 * services; today's date in both calendars; and the happiness meter (GOV-2). Works for visitors,
 * who see public content and sign in to raise tickets.
 */
export default async function PortalHomePage({ params, searchParams }: PageProps<"/[locale]">) {
  const { locale: rawLocale } = await params;
  const locale = rawLocale as Locale;
  setRequestLocale(locale);
  const other = otherLocale(locale);
  const [t, tWelcome, tOther, display, { session, tenant }] = await Promise.all([
    getTranslations({ locale, namespace: "portal" }),
    getTranslations({ locale, namespace: "welcome" }),
    getTranslations({ locale: other, namespace: "portal" }),
    getDisplayFormat(),
    optionalSession(),
  ]);
  const token = session?.data.accessToken ?? "";
  const q = typeof (await searchParams).q === "string" ? String((await searchParams).q).slice(0, 200) : "";
  const [open, catalog, voice] = await Promise.all([
    session ? getJsonAs<PortalTicketPage>("/api/v1/me/tickets?open=true&pageSize=4", token, tenant) : Promise.resolve(null),
    getJsonAs<CatalogBrowse>("/api/v1/catalog/published?topLevel=true", token, tenant),
    loadVoiceSettings(token, tenant),
  ]);
  const dateDisplay = { locale, ...display };
  const signIn = `/auth/login?${new URLSearchParams({ locale, returnTo: `/${locale}` })}`;
  const now = new Date();
  const calendars = ([
    { calendar: "gregory", name: tWelcome("gregorian") },
    { calendar: "islamic-umalqura", name: tWelcome("hijri") },
  ] as { calendar: CalendarSystem; name: string }[]).sort((a, b) => Number(b.calendar === display.calendar) - Number(a.calendar === display.calendar));
  const actions = [
    { key: "report", href: "/my-incidents/new", glyph: "🛠️" },
    { key: "request", href: "/catalog", glyph: "📋" },
    { key: "contact", href: "/contact", glyph: "💬" },
  ] as const;
  const services = (catalog?.services ?? []).slice(0, 6);

  return (
    <div className="space-y-10">
      <section aria-labelledby="portal-heading" className="space-y-4 rounded-xl bg-surface-muted px-4 py-8 sm:px-8">
        <h1 id="portal-heading" className="text-3xl font-bold text-brand md:text-4xl">{t("heading")}</h1>
        <p lang={other} dir={getDirection(other)} className="text-lg text-ink-muted">{tOther("heading")}</p>
        <PortalSearch initialQuery={q} display={dateDisplay} signedIn={!!session} />
      </section>

      <section aria-labelledby="actions-heading" className="space-y-3">
        <h2 id="actions-heading" className="text-xl font-bold">{t("actions.heading")}</h2>
        <ul className="grid gap-3 sm:grid-cols-3">
          {actions.map((action) => (
            <li key={action.key}>
              <Link href={action.href} data-testid={`portal-${action.key}`}
                className="flex h-full min-h-20 items-start gap-3 rounded-lg border border-line p-4 hover:border-brand focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
                <span aria-hidden="true" className="text-2xl">{action.glyph}</span>
                <span>
                  <span className="block font-semibold text-brand">{t(`actions.${action.key}`)}</span>
                  <span className="block text-sm text-ink-muted">{t(`actions.${action.key}Hint`)}</span>
                </span>
              </Link>
            </li>
          ))}
        </ul>
      </section>

      {session ? (
        <section aria-labelledby="open-heading" className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="open-heading" className="text-xl font-bold">{t("open.heading")}</h2>
            <Link href="/my-tickets" className="font-semibold text-brand underline">{t("open.all")}</Link>
          </div>
          {!open ? <p role="alert">{t("open.unavailable")}</p> : open.items.length === 0 ? <p>{t("open.empty")}</p> : (
            <TicketProgressList tickets={open.items} display={dateDisplay} />
          )}
        </section>
      ) : (
        <section aria-labelledby="visitor-heading" className="space-y-2 rounded-lg border border-line p-4">
          <h2 id="visitor-heading" className="text-xl font-bold">{t("visitor.heading")}</h2>
          <p>{t("visitor.intro")}</p>
          <p className="flex flex-wrap gap-4">
            <a href={signIn} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("visitor.signIn")}</a>
            {voice?.anonymousEnabled && <Link href="/track" className="self-center font-semibold text-brand underline">{t("visitor.track")}</Link>}
          </p>
        </section>
      )}

      {services.length > 0 && (
        <section aria-labelledby="services-heading" className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="services-heading" className="text-xl font-bold">{t("services.heading")}</h2>
            <Link href="/catalog" className="font-semibold text-brand underline">{t("services.all")}</Link>
          </div>
          <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {services.map((s) => (
              <li key={s.id}>
                <Link href={`/catalog/${s.id}`} className="flex h-full items-start gap-3 rounded-lg border border-line p-3 hover:border-brand focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
                  <span aria-hidden="true" className="text-xl">{iconGlyph[s.icon ?? "service"] ?? iconGlyph.service}</span>
                  <span>
                    <span className="block font-semibold" dir="auto">{textOf(s.name, locale)}</span>
                    {textOf(s.summary, locale) && <span className="block text-sm text-ink-muted" dir="auto">{textOf(s.summary, locale)}</span>}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      <div className="grid gap-6 md:grid-cols-2">
        <section aria-labelledby="today-heading" className="space-y-3">
          <h2 id="today-heading" className="text-xl font-bold">{tWelcome("todayHeading")}</h2>
          <dl className="grid gap-3">
            {calendars.map(({ calendar, name }, index) => (
              <div key={calendar} className="rounded-lg border border-line bg-surface p-4" data-testid={index === 0 ? "today-primary" : "today-secondary"}>
                <dt className="text-sm text-ink-muted">{name} · {index === 0 ? tWelcome("yourCalendar") : tWelcome("otherCalendar")}</dt>
                <dd className="font-semibold">{formatLongDate(now, { locale, digits: display.digits, timeZone: display.timeZone, calendar })}</dd>
              </div>
            ))}
          </dl>
          <p className="flex flex-wrap gap-4">
            <Link href="/help" className="font-semibold text-brand underline">{t("help")}</Link>
            <Link href="/status" className="font-semibold text-brand underline">{t("status")}</Link>
          </p>
        </section>
        {voice?.happiness.enabled && voice.happiness.inPortal && (
          <PortalHappiness settings={voice.happiness} csrfToken={session?.data.csrfToken ?? null} />
        )}
      </div>
    </div>
  );
}

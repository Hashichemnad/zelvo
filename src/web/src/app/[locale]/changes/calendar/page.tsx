import { getTranslations, setRequestLocale } from "next-intl/server";
import { ChangeStageBadge, RiskBadge } from "@/components/changes/common";
import { Localized } from "@/components/directory/common";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { ChangeCalendar } from "@/lib/changes";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/changes/calendar">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "changes.calendar" });
  return { title: t("title") };
}

/** The first day of the month of `yyyy-mm`, or of this month. */
function monthOf(value: string | undefined): Date {
  const match = value && /^(\d{4})-(\d{2})$/.exec(value);
  const now = new Date();
  const year = match ? Number(match[1]) : now.getUTCFullYear();
  const month = match ? Number(match[2]) - 1 : now.getUTCMonth();
  return new Date(Date.UTC(year, Math.min(Math.max(month, 0), 11), 1));
}

const key = (d: Date) => `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, "0")}`;

/**
 * The change calendar (task H3, changes.read): a month of changes by their planned window, each
 * marked when it conflicts with another change on the same configuration item or a blackout
 * period, and the month's blackout periods. A table, so keyboard and screen-reader users read it
 * like everyone else.
 */
export default async function ChangeCalendarPage({ params, searchParams }: PageProps<"/[locale]/changes/calendar">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/changes/calendar`);
  const [t, tList, tKinds] = await Promise.all([
    getTranslations({ locale, namespace: "changes.calendar" }), getTranslations({ locale, namespace: "changes.list" }), getTranslations({ locale, namespace: "changes.kinds" }),
  ]);
  if (!user?.permissions.includes("changes.read")) {
    return <p role="alert">{user ? tList("noAccess") : tList("unavailable")}</p>;
  }
  const raw = (await searchParams).month;
  const from = monthOf(typeof raw === "string" ? raw : undefined);
  const to = new Date(Date.UTC(from.getUTCFullYear(), from.getUTCMonth() + 1, 1));
  const previous = new Date(Date.UTC(from.getUTCFullYear(), from.getUTCMonth() - 1, 1));
  const [calendar, format] = await Promise.all([
    getJsonAs<ChangeCalendar>(`/api/v1/changes/calendar?${new URLSearchParams({ from: from.toISOString(), to: to.toISOString() })}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const display = { locale, ...format };
  const monthName = new Intl.DateTimeFormat(locale === "ar" ? "ar-AE" : "en-GB", { month: "long", year: "numeric", timeZone: "UTC", calendar: format.calendar, numberingSystem: format.digits }).format(from);
  const l = locale as Locale;

  return (
    <section aria-labelledby="calendar-heading" className="space-y-6">
      <Link href="/changes" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{tList("title")}</Link>
      <h1 id="calendar-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      <nav aria-label={t("months")} className="flex flex-wrap items-center gap-3">
        <Link href={`/changes/calendar?month=${key(previous)}`} className="font-semibold text-brand underline">{t("previous")}</Link>
        <p className="text-xl font-semibold" aria-live="polite">{monthName}</p>
        <Link href={`/changes/calendar?month=${key(to)}`} className="font-semibold text-brand underline">{t("next")}</Link>
      </nav>
      {!calendar ? <p role="alert">{tList("unavailable")}</p> : (
        <>
          <h2 className="text-xl font-semibold">{t("changes")}</h2>
          {calendar.changes.length === 0 ? <p>{t("noChanges")}</p> : (
            <div className="overflow-x-auto">
              <table className={tableClass} data-testid="change-calendar">
                <caption className="sr-only">{t("changes")}</caption>
                <thead>
                  <tr>
                    <th scope="col" className={cellClass}>{t("window")}</th>
                    <th scope="col" className={cellClass}>{tList("number")}</th>
                    <th scope="col" className={cellClass}>{tList("titleColumn")}</th>
                    <th scope="col" className={cellClass}>{tList("kind")}</th>
                    <th scope="col" className={cellClass}>{tList("stage")}</th>
                    <th scope="col" className={cellClass}>{tList("risk")}</th>
                    <th scope="col" className={cellClass}>{t("items")}</th>
                    <th scope="col" className={cellClass}>{t("conflict")}</th>
                  </tr>
                </thead>
                <tbody>
                  {calendar.changes.map((c) => (
                    <tr key={c.id}>
                      <td className={cellClass}>{formatDateTime(new Date(c.start), display)} – {formatDateTime(new Date(c.end), display)}</td>
                      <td className={cellClass}><Link href={`/changes/${c.id}`} className="font-mono font-semibold text-brand underline" dir="ltr">{c.number}</Link></td>
                      <td className={cellClass} dir="auto">{c.title}</td>
                      <td className={cellClass}>{tKinds(c.kind)}</td>
                      <td className={cellClass}><ChangeStageBadge stage={c.stage} /></td>
                      <td className={cellClass}><RiskBadge level={c.riskLevel} /></td>
                      <td className={cellClass}>{c.items.map((i) => i.name).join(", ") || "—"}</td>
                      <td className={cellClass}>{c.conflict ? <span className="font-semibold text-warn">{t("conflictYes")}</span> : t("conflictNo")}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <h2 className="text-xl font-semibold">{t("blackouts")}</h2>
          {calendar.blackouts.length === 0 ? <p>{t("noBlackouts")}</p> : (
            <ul className="space-y-1" data-testid="calendar-blackouts">
              {calendar.blackouts.map((b) => (
                <li key={b.id}>
                  <span className="font-semibold"><Localized text={b.name} locale={l} /></span>{" "}
                  {formatDateTime(new Date(b.startsAt), display)} – {formatDateTime(new Date(b.endsAt), display)}{" "}
                  <span className="text-ink-muted">{b.item ? t("blackoutItem", { name: b.item.name }) : t("blackoutAll")}</span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}

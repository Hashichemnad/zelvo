import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { textOf } from "@/lib/directory";
import { formatNumber } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";
import type { VoiceReport, VoiceSettings } from "@/lib/voice";

export async function generateMetadata({ params }: PageProps<"/[locale]/customer-voice/report">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "voice.report" });
  return { title: t("title") };
}

const datePattern = /^\d{4}-\d{2}-\d{2}$/;

/**
 * Happiness and customer-voice reporting (GOV-3, task C6): records per type, state, channel and
 * service, complaint severities, root causes and confirmations, suggestion reviews, happiness by
 * record type and channel, and the month-by-month trend, with a CSV export. Bars are drawn with
 * plain elements and every value is also written as text and in tables.
 */
export default async function VoiceReportPage({ params, searchParams }: PageProps<"/[locale]/customer-voice/report">) {
  const { locale: raw } = await params;
  const locale = raw as Locale;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/customer-voice/report`);
  const t = await getTranslations({ locale, namespace: "voice.report" });
  const tVoice = await getTranslations({ locale, namespace: "voice" });
  const tStates = await getTranslations({ locale, namespace: "incidents.states" });
  const tChannels = await getTranslations({ locale, namespace: "incidents.channels" });
  if (!user?.permissions.includes("voice.report")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const search = await searchParams;
  const from = typeof search.from === "string" && datePattern.test(search.from) ? search.from : "";
  const to = typeof search.to === "string" && datePattern.test(search.to) ? search.to : "";
  const query = new URLSearchParams({ ...(from ? { from } : {}), ...(to ? { to } : {}) });
  const [report, settings, format] = await Promise.all([
    getJsonAs<VoiceReport>(`/api/v1/customer-voice/report?${query}`, session.data.accessToken, tenant),
    getJsonAs<VoiceSettings>("/api/v1/settings/customer-voice", session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const n = (value: number) => formatNumber(value, locale, format.digits);
  const pct = (value: number | null) => (value === null ? t("noRatings") : `${formatNumber(value, locale, format.digits)} %`);
  const cell = "border-b border-line px-2 py-1 text-start";
  const input = "rounded-md border border-line bg-surface px-3 py-2";
  const keyName = (kind: "severity" | "rootCause" | "confirmation" | "review", key: string) => {
    if (kind === "severity") return textOf(settings?.severities.find((s) => String(s.level) === key)?.name, locale) || key;
    if (kind === "rootCause") return textOf(settings?.rootCauses.find((r) => r.key === key)?.name, locale) || key;
    return tVoice(kind === "confirmation" ? `confirmations.${key}` : `reviews.${key}`);
  };
  const recordTypeName = (key: string) => (["Complaint", "Inquiry", "Suggestion", "Incident", "ServiceRequest", "Portal"].includes(key) ? t(`recordTypes.${key}`) : key);
  const bars = (heading: string, rows: { key: string; label: string; count: number }[]) => {
    const max = Math.max(1, ...rows.map((r) => r.count));
    return (
      <section aria-label={heading} className="space-y-2">
        <h3 className="font-semibold">{heading}</h3>
        {rows.length === 0 ? <p className="text-ink-muted">{t("none")}</p> : (
          <ul className="space-y-1">
            {rows.map((r) => (
              <li key={r.key} className="grid grid-cols-[minmax(8rem,14rem)_1fr_auto] items-center gap-2 text-sm">
                <span>{r.label}</span>
                <span aria-hidden="true" className="h-3 rounded-full bg-surface-muted"><span className="block h-3 rounded-full bg-brand" style={{ width: `${(r.count / max) * 100}%` }} /></span>
                <span className="tabular-nums">{n(r.count)}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
    );
  };

  return (
    <section aria-labelledby="report-heading" className="space-y-8">
      <Link href="/customer-voice" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <h1 id="report-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-2xl">{t("intro")}</p>
      <form method="get" className="flex flex-wrap items-end gap-3" aria-label={t("period")}>
        <div className="space-y-1">
          <label htmlFor="report-from" className="block text-sm">{t("from")}</label>
          <input id="report-from" type="date" name="from" defaultValue={from} className={input} />
        </div>
        <div className="space-y-1">
          <label htmlFor="report-to" className="block text-sm">{t("to")}</label>
          <input id="report-to" type="date" name="to" defaultValue={to} className={input} />
        </div>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("show")}</button>
        <a href={`/bff/api/v1/customer-voice/report/csv?${query}`} className="font-semibold text-brand underline">{t("csv")}</a>
      </form>
      {!report ? <p role="alert">{t("unavailable")}</p> : (
        <>
          <section aria-labelledby="report-summary" className="space-y-3">
            <h2 id="report-summary" className="text-xl font-semibold">{t("summary")}</h2>
            <dl className="grid gap-3 sm:grid-cols-4">
              {report.types.map((r) => (
                <div key={r.type} className="rounded-lg border border-line p-3">
                  <dt className="text-sm text-ink-muted">{tVoice(`types.${r.type}`)}</dt>
                  <dd className="text-2xl font-bold">{n(r.total)}</dd>
                  <dd className="text-sm">{t("openResolved", { open: n(r.open), resolved: n(r.resolved) })}</dd>
                  {r.averageHoursToResolve !== null && <dd className="text-sm">{t("hoursToResolve", { hours: n(r.averageHoursToResolve) })}</dd>}
                  {r.anonymous > 0 && <dd className="text-sm">{t("anonymousCount", { count: n(r.anonymous) })}</dd>}
                </div>
              ))}
              <div className="rounded-lg border border-brand p-3" data-testid="report-happiness">
                <dt className="text-sm text-ink-muted">{t("happiness")}</dt>
                <dd className="text-2xl font-bold">{pct(report.happiness.happinessPercent)}</dd>
                <dd className="text-sm">{t("ratings", { count: n(report.happiness.ratings) })}</dd>
              </div>
            </dl>
            <p className="text-sm text-ink-muted">{t("happinessExplained")}</p>
          </section>

          <section aria-labelledby="report-trend" className="space-y-2">
            <h2 id="report-trend" className="text-xl font-semibold">{t("trend")}</h2>
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-sm">
                <thead><tr>
                  <th scope="col" className={cell}>{t("month")}</th>
                  <th scope="col" className={cell}>{tVoice("types.Complaint")}</th>
                  <th scope="col" className={cell}>{tVoice("types.Inquiry")}</th>
                  <th scope="col" className={cell}>{tVoice("types.Suggestion")}</th>
                  <th scope="col" className={cell}>{t("ratingsColumn")}</th>
                  <th scope="col" className={cell}>{t("happiness")}</th>
                </tr></thead>
                <tbody>
                  {report.months.map((m) => (
                    <tr key={m.month}>
                      <th scope="row" className={cell}><span dir="ltr">{m.month}</span></th>
                      <td className={cell}>{n(m.complaints)}</td>
                      <td className={cell}>{n(m.inquiries)}</td>
                      <td className={cell}>{n(m.suggestions)}</td>
                      <td className={cell}>{n(m.ratings)}</td>
                      <td className={cell}>
                        {m.happinessPercent === null ? "—" : (
                          <span className="flex items-center gap-2">
                            <span aria-hidden="true" className="inline-block h-2 w-20 rounded-full bg-surface-muted"><span className="block h-2 rounded-full bg-ok" style={{ width: `${m.happinessPercent}%` }} /></span>
                            {pct(m.happinessPercent)}
                          </span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <div className="grid gap-8 md:grid-cols-2">
            {bars(t("bySeverity"), report.severities.map((r) => ({ key: r.key, label: keyName("severity", r.key), count: r.count })))}
            {bars(t("byRootCause"), report.rootCauses.map((r) => ({ key: r.key, label: keyName("rootCause", r.key), count: r.count })))}
            {bars(t("byConfirmation"), report.confirmations.map((r) => ({ key: r.key, label: keyName("confirmation", r.key), count: r.count })))}
            {bars(t("byReview"), report.reviews.map((r) => ({ key: r.key, label: keyName("review", r.key), count: r.count })))}
            {bars(t("byChannel"), report.channels.map((r) => ({ key: `${r.type}-${r.channel}`, label: `${tVoice(`types.${r.type}`)} · ${tChannels(r.channel)}`, count: r.count })))}
            {bars(t("byState"), report.states.map((r) => ({ key: `${r.type}-${r.state}`, label: `${tVoice(`types.${r.type}`)} · ${tStates(r.state)}`, count: r.count })))}
          </div>

          <section aria-labelledby="report-happiness-groups" className="space-y-2">
            <h2 id="report-happiness-groups" className="text-xl font-semibold">{t("happinessBy")}</h2>
            <div className="grid gap-6 md:grid-cols-2">
              {[{ heading: t("byRecordType"), rows: report.happiness.byRecordType, name: recordTypeName }, { heading: t("byChannel"), rows: report.happiness.byChannel, name: (k: string) => tChannels(k) }].map((g) => (
                <div key={g.heading} className="overflow-x-auto">
                  <table className="w-full border-collapse text-sm">
                    <caption className="text-start font-semibold">{g.heading}</caption>
                    <thead><tr><th scope="col" className={cell}>{t("group")}</th><th scope="col" className={cell}>{t("ratingsColumn")}</th><th scope="col" className={cell}>{t("happiness")}</th></tr></thead>
                    <tbody>
                      {g.rows.map((r) => <tr key={r.key}><th scope="row" className={cell}>{g.name(r.key)}</th><td className={cell}>{n(r.count)}</td><td className={cell}>{pct(r.happinessPercent)}</td></tr>)}
                    </tbody>
                  </table>
                </div>
              ))}
            </div>
          </section>

          <section aria-labelledby="report-services" className="space-y-2">
            <h2 id="report-services" className="text-xl font-semibold">{t("byService")}</h2>
            {report.services.length === 0 ? <p className="text-ink-muted">{t("none")}</p> : (
              <div className="overflow-x-auto">
                <table className="w-full border-collapse text-sm">
                  <thead><tr>
                    <th scope="col" className={cell}>{t("service")}</th>
                    <th scope="col" className={cell}>{tVoice("types.Complaint")}</th>
                    <th scope="col" className={cell}>{tVoice("types.Inquiry")}</th>
                    <th scope="col" className={cell}>{tVoice("types.Suggestion")}</th>
                    <th scope="col" className={cell}>{t("ratingsColumn")}</th>
                    <th scope="col" className={cell}>{t("happiness")}</th>
                  </tr></thead>
                  <tbody>
                    {report.services.map((s) => (
                      <tr key={s.id}>
                        <th scope="row" className={cell}>{textOf(s.name, locale) || s.code}</th>
                        <td className={cell}>{n(s.complaints)}</td>
                        <td className={cell}>{n(s.inquiries)}</td>
                        <td className={cell}>{n(s.suggestions)}</td>
                        <td className={cell}>{n(s.ratings)}</td>
                        <td className={cell}>{pct(s.happinessPercent)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </section>
  );
}

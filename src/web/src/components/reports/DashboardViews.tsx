import { getTranslations } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import {
  bucketLabel, dashboards, formatDay, formatFigure, nameIn, type AgentsReport, type BacklogReport, type HappinessGroup, type HappinessReport, type KeyCount,
  type NamedCount, type OverviewReport, type ReportDisplay, type SlaReport, type SlaRow, type VolumesReport,
} from "@/lib/reports";
import { BarList, KpiTile, TrendChart, type BarRow } from "./ReportCharts";

/**
 * The bodies of the dashboards (task F2), built on the server from the API's reports. Labels come
 * from the translation files, numbers and dates are in the reader's digits and calendar (LANG-8).
 */

type T = Awaited<ReturnType<typeof getTranslations<"reports">>>;

function helpers(t: T, display: ReportDisplay) {
  const n = (v: number) => formatFigure(v, display);
  const pct = (v: number | null) => (v === null ? t("labels.noData") : `${n(v)} %`);
  const keyLabel = (group: "types" | "channels" | "states" | "ages" | "priority", key: string) => {
    if (key === "none") return t("labels.none");
    if (group === "priority") return t("labels.priority", { value: n(Number(key)) });
    return t.has(`${group}.${key}`) ? t(`${group}.${key}`) : key;
  };
  const named = (name: { en: string | null; ar: string | null } | null, id: string | null) =>
    nameIn(name, display.locale) || (id ? t("labels.removed") : t("labels.none"));
  const countRows = (rows: KeyCount[], group: "types" | "channels" | "states" | "ages" | "priority"): BarRow[] =>
    rows.map((r) => ({ key: r.key, label: keyLabel(group, r.key), value: { value: r.count, text: n(r.count) } }));
  const namedRows = (rows: NamedCount[]): BarRow[] =>
    rows.map((r, i) => ({ key: r.id ?? `none-${i}`, label: named(r.name, r.id), value: { value: r.count, text: n(r.count) } }));
  return { n, pct, keyLabel, named, countRows, namedRows };
}

function Bars({ t, caption, rows, percent = false }: { t: T; caption: string; rows: BarRow[]; percent?: boolean }) {
  return <BarList caption={caption} rows={rows} max={percent ? 100 : undefined} labelHeading={t("labels.name")} valueHeading={percent ? t("labels.percent") : t("labels.count")} empty={t("labels.empty")} />;
}

export async function OverviewView({ report, display, own }: { report: OverviewReport; display: ReportDisplay; own: boolean }) {
  const t = await getTranslations({ locale: display.locale, namespace: "reports" });
  const { n, pct } = helpers(t, display);
  const slaTarget = report.targets.slaTargetPercent;
  const happinessTarget = report.targets.happinessTargetPercent;
  const state = (value: number | null, target: number) => (value === null ? undefined : value >= target ? "ok" : "bad");
  const targetNote = (value: number | null, target: number) =>
    `${t("figures.target", { value: `${n(target)} %` })}${value === null ? "" : ` · ${value >= target ? t("figures.meetsTarget") : t("figures.missesTarget")}`}`;
  return (
    <div className="space-y-6">
      <p className="text-sm">{t("period", { from: formatDay(report.from, display), to: formatDay(report.to, display) })}</p>
      <dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4" data-testid="report-kpis">
        <KpiTile label={t("figures.created")} value={n(report.created)} testId="kpi-created" />
        <KpiTile label={t("figures.resolved")} value={n(report.resolved)} testId="kpi-resolved" />
        <KpiTile label={t("figures.open")} value={n(report.open)} testId="kpi-open" />
        <KpiTile label={t("figures.medianResponse")} value={report.medianResponseMinutes === null ? t("labels.noData") : t("figures.minutes", { value: n(report.medianResponseMinutes) })} />
        <KpiTile label={t("figures.responseSla")} value={pct(report.responsePercent)} note={targetNote(report.responsePercent, slaTarget)} state={state(report.responsePercent, slaTarget)} testId="kpi-response" />
        <KpiTile label={t("figures.resolutionSla")} value={pct(report.resolutionPercent)} note={targetNote(report.resolutionPercent, slaTarget)} state={state(report.resolutionPercent, slaTarget)} />
        <KpiTile label={t("figures.happiness")} value={pct(report.happinessPercent)} note={`${t("figures.ratings", { count: n(report.ratings) })} · ${targetNote(report.happinessPercent, happinessTarget)}`} state={state(report.happinessPercent, happinessTarget)} testId="kpi-happiness" />
      </dl>
      <p className="text-sm text-ink-muted">{t("labels.happinessExplained")}</p>
      {!own && (
        <ul className="grid gap-4 sm:grid-cols-2">
          {dashboards.map((d) => (
            <li key={d.slug} className="rounded-lg border border-line bg-surface p-4">
              <h2 className="text-lg font-bold">
                <Link href={`/reports/${d.slug}`} className="text-brand underline-offset-4 hover:underline">{t(`dashboards.${d.kind}.title`)}</Link>
              </h2>
              <p className="text-sm text-ink-muted">{t(`dashboards.${d.kind}.description`)}</p>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export async function VolumesView({ report, display }: { report: VolumesReport; display: ReportDisplay }) {
  const t = await getTranslations({ locale: display.locale, namespace: "reports" });
  const { n, countRows, namedRows } = helpers(t, display);
  const v = (x: number) => ({ value: x, text: n(x) });
  return (
    <div className="space-y-8">
      <p className="text-sm">{t("period", { from: formatDay(report.from, display), to: formatDay(report.to, display) })}</p>
      <dl className="grid gap-3 sm:grid-cols-3">
        <KpiTile label={t("figures.created")} value={n(report.created)} testId="kpi-created" />
        <KpiTile label={t("figures.resolved")} value={n(report.resolved)} />
        <KpiTile label={t("figures.closed")} value={n(report.closed)} />
      </dl>
      <TrendChart caption={t("headings.trend")} bucketHeading={t("labels.bucket")} empty={t("labels.empty")} testId="report-trend"
        buckets={report.series.map((p) => bucketLabel(p.bucket, report.interval, display))}
        series={[
          { key: "created", name: t("figures.created"), colour: "bg-brand", values: report.series.map((p) => v(p.created)) },
          { key: "resolved", name: t("figures.resolved"), colour: "bg-ok", values: report.series.map((p) => v(p.resolved)) },
          { key: "closed", name: t("figures.closed"), colour: "bg-ink-muted", values: report.series.map((p) => v(p.closed)) },
        ]} />
      <div className="grid gap-8 md:grid-cols-2">
        <Bars t={t} caption={t("headings.byType")} rows={countRows(report.byType, "types")} />
        <Bars t={t} caption={t("headings.byChannel")} rows={countRows(report.byChannel, "channels")} />
        <Bars t={t} caption={t("headings.byPriority")} rows={countRows(report.byPriority, "priority")} />
        <Bars t={t} caption={t("headings.byGroup")} rows={namedRows(report.byGroup)} />
        <Bars t={t} caption={t("headings.byCategory")} rows={namedRows(report.byCategory)} />
      </div>
    </div>
  );
}

export async function BacklogView({ report, display }: { report: BacklogReport; display: ReportDisplay }) {
  const t = await getTranslations({ locale: display.locale, namespace: "reports" });
  const { n, countRows, namedRows } = helpers(t, display);
  return (
    <div className="space-y-8">
      <dl className="grid gap-3 sm:grid-cols-2">
        <KpiTile label={t("figures.open")} value={n(report.open)} testId="kpi-open" />
        <KpiTile label={t("figures.unassigned")} value={n(report.unassigned)} />
      </dl>
      <TrendChart caption={t("headings.backlogTrend")} bucketHeading={t("labels.bucket")} empty={t("labels.empty")} testId="report-trend"
        buckets={report.trend.map((p) => bucketLabel(p.bucket, report.interval, display))}
        series={[{ key: "open", name: t("figures.open"), colour: "bg-warn", values: report.trend.map((p) => ({ value: p.open, text: n(p.open) })) }]} />
      <div className="grid gap-8 md:grid-cols-2">
        <Bars t={t} caption={t("headings.byState")} rows={countRows(report.byState, "states")} />
        <Bars t={t} caption={t("headings.byAge")} rows={countRows(report.byAge, "ages")} />
        <Bars t={t} caption={t("headings.byType")} rows={countRows(report.byType, "types")} />
        <Bars t={t} caption={t("headings.byPriority")} rows={countRows(report.byPriority, "priority")} />
        <Bars t={t} caption={t("headings.byGroup")} rows={namedRows(report.byGroup)} />
      </div>
    </div>
  );
}

export async function SlaView({ report, display }: { report: SlaReport; display: ReportDisplay }) {
  const t = await getTranslations({ locale: display.locale, namespace: "reports" });
  const { n, pct, keyLabel, named } = helpers(t, display);
  const target = report.targetPercent;
  const state = (v: number | null) => (v === null ? undefined : v >= target ? "ok" : "bad");
  const cell = "border-b border-line px-2 py-1 text-start";
  const table = (caption: string, rows: SlaRow[], label: (r: SlaRow) => string) => (
    <div className="overflow-x-auto" tabIndex={0}>
      {rows.length === 0 ? <><h3 className="font-semibold">{caption}</h3><p className="text-ink-muted">{t("labels.empty")}</p></> : (
        <table className="w-full border-collapse text-sm">
          <caption className="pb-1 text-start font-semibold">{caption}</caption>
          <thead><tr>
            <th scope="col" className={cell}>{t("labels.name")}</th>
            <th scope="col" className={`${cell} text-end`}>{t("figures.responseSla")}</th>
            <th scope="col" className={`${cell} text-end`}>{t("labels.metBreached")}</th>
            <th scope="col" className={`${cell} text-end`}>{t("figures.resolutionSla")}</th>
            <th scope="col" className={`${cell} text-end`}>{t("labels.metBreached")}</th>
          </tr></thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.key}>
                <th scope="row" className={`${cell} font-normal`}><bdi>{label(r)}</bdi></th>
                <td className={`${cell} text-end tabular-nums`}>{pct(r.response.percent)}</td>
                <td className={`${cell} text-end tabular-nums`}>{n(r.response.met)} / {n(r.response.breached)}</td>
                <td className={`${cell} text-end tabular-nums`}>{pct(r.resolution.percent)}</td>
                <td className={`${cell} text-end tabular-nums`}>{n(r.resolution.met)} / {n(r.resolution.breached)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
  const targetText = t("figures.target", { value: `${n(target)} %` });
  return (
    <div className="space-y-8">
      <dl className="grid gap-3 sm:grid-cols-2">
        <KpiTile label={t("figures.responseSla")} value={pct(report.response.percent)} state={state(report.response.percent)} testId="kpi-response"
          note={`${targetText} · ${t("labels.clocks", { met: n(report.response.met), breached: n(report.response.breached), running: n(report.response.running) })}`} />
        <KpiTile label={t("figures.resolutionSla")} value={pct(report.resolution.percent)} state={state(report.resolution.percent)}
          note={`${targetText} · ${t("labels.clocks", { met: n(report.resolution.met), breached: n(report.resolution.breached), running: n(report.resolution.running) })}`} />
      </dl>
      <p className="text-sm text-ink-muted">{t("labels.slaExplained")}</p>
      <TrendChart caption={t("headings.slaTrend")} bucketHeading={t("labels.bucket")} empty={t("labels.empty")} max={100} testId="report-trend"
        target={{ value: target, text: targetText }}
        buckets={report.trend.map((p) => bucketLabel(p.bucket, report.interval, display))}
        series={[
          { key: "response", name: t("figures.responseSla"), colour: "bg-brand", values: report.trend.map((p) => ({ value: p.responsePercent, text: pct(p.responsePercent) })) },
          { key: "resolution", name: t("figures.resolutionSla"), colour: "bg-ok", values: report.trend.map((p) => ({ value: p.resolutionPercent, text: pct(p.resolutionPercent) })) },
        ]} />
      {table(t("headings.byPriority"), report.byPriority, (r) => keyLabel("priority", r.key))}
      {table(t("headings.byType"), report.byType, (r) => keyLabel("types", r.key))}
      {table(t("headings.byGroup"), report.byGroup, (r) => named(r.name, r.id))}
      {table(t("headings.byPolicy"), report.byPolicy, (r) => named(r.name, r.id))}
    </div>
  );
}

export async function AgentsView({ report, display }: { report: AgentsReport; display: ReportDisplay }) {
  const t = await getTranslations({ locale: display.locale, namespace: "reports" });
  const { n, pct } = helpers(t, display);
  const cell = "border-b border-line px-2 py-1 text-start";
  const num = `${cell} text-end tabular-nums`;
  return (
    <div className="space-y-4">
      <p className="text-sm">{t("period", { from: formatDay(report.from, display), to: formatDay(report.to, display) })}</p>
      <p className="text-sm text-ink-muted">{t("labels.agentsExplained")}</p>
      {report.agents.length === 0 ? <p className="text-ink-muted">{t("labels.empty")}</p> : (
        <div className="overflow-x-auto" tabIndex={0}>
          <table className="w-full border-collapse text-sm" data-testid="agents-table">
            <caption className="pb-1 text-start font-semibold">{t("dashboards.Agents.title")}</caption>
            <thead><tr>
              <th scope="col" className={cell}>{t("labels.agent")}</th>
              <th scope="col" className={`${cell} text-end`}>{t("labels.assigned")}</th>
              <th scope="col" className={`${cell} text-end`}>{t("figures.resolved")}</th>
              <th scope="col" className={`${cell} text-end`}>{t("labels.openNow")}</th>
              <th scope="col" className={`${cell} text-end`}>{t("figures.medianResponse")}</th>
              <th scope="col" className={`${cell} text-end`}>{t("figures.medianResolution")}</th>
              <th scope="col" className={`${cell} text-end`}>{t("labels.slaMet")}</th>
              <th scope="col" className={`${cell} text-end`}>{t("labels.reopened")}</th>
              <th scope="col" className={`${cell} text-end`}>{t("figures.happiness")}</th>
            </tr></thead>
            <tbody>
              {report.agents.map((a) => (
                <tr key={a.id}>
                  <th scope="row" className={`${cell} font-normal`}><bdi>{a.name}</bdi></th>
                  <td className={num}>{n(a.assigned)}</td>
                  <td className={num}>{n(a.resolved)}</td>
                  <td className={num}>{n(a.openNow)}</td>
                  <td className={num}>{a.medianResponseMinutes === null ? t("labels.noData") : t("figures.minutes", { value: n(a.medianResponseMinutes) })}</td>
                  <td className={num}>{a.medianResolutionHours === null ? t("labels.noData") : t("figures.hours", { value: n(a.medianResolutionHours) })}</td>
                  <td className={num}>{pct(a.slaPercent)}</td>
                  <td className={num}>{pct(a.reopenPercent)}</td>
                  <td className={num}>{pct(a.happinessPercent)} ({n(a.ratings)})</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

export async function HappinessView({ report, display }: { report: HappinessReport; display: ReportDisplay }) {
  const t = await getTranslations({ locale: display.locale, namespace: "reports" });
  const { n, pct, keyLabel, named } = helpers(t, display);
  const target = report.targetPercent;
  const targetText = t("figures.target", { value: `${n(target)} %` });
  const rows = (groups: HappinessGroup[], label: (g: HappinessGroup) => string): BarRow[] =>
    groups.map((g) => ({ key: g.key, label: `${label(g)} (${t("figures.ratings", { count: n(g.ratings) })})`, value: { value: g.happinessPercent, text: pct(g.happinessPercent) } }));
  return (
    <div className="space-y-8">
      <dl className="grid gap-3 sm:grid-cols-2">
        <KpiTile label={t("figures.happiness")} value={pct(report.happinessPercent)} testId="kpi-happiness"
          state={report.happinessPercent === null ? undefined : report.happinessPercent >= target ? "ok" : "bad"} note={targetText} />
        <KpiTile label={t("figures.ratingsHeading")} value={n(report.ratings)} />
      </dl>
      <p className="text-sm text-ink-muted">{t("labels.happinessExplained")}</p>
      <TrendChart caption={t("headings.happinessTrend")} bucketHeading={t("labels.bucket")} empty={t("labels.empty")} max={100} testId="report-trend"
        target={{ value: target, text: targetText }}
        buckets={report.trend.map((p) => bucketLabel(p.bucket, report.interval, display))}
        series={[{ key: "happiness", name: t("figures.happiness"), colour: "bg-ok", values: report.trend.map((p) => ({ value: p.happinessPercent, text: pct(p.happinessPercent) })) }]} />
      <div className="grid gap-8 md:grid-cols-2">
        <Bars t={t} caption={t("headings.scores")} rows={report.scores.map((s) => ({ key: `${s.points}-${s.score}`, label: t("labels.score", { score: n(s.score), points: n(s.points) }), value: { value: s.count, text: n(s.count) } }))} />
        <Bars t={t} caption={t("headings.byType")} percent rows={rows(report.byType, (g) => keyLabel("types", g.key))} />
        <Bars t={t} caption={t("headings.byChannel")} percent rows={rows(report.byChannel, (g) => keyLabel("channels", g.key))} />
        <Bars t={t} caption={t("headings.byGroup")} percent rows={rows(report.byGroup, (g) => named(g.name, g.id))} />
        <Bars t={t} caption={t("headings.byService")} percent rows={rows(report.byService, (g) => named(g.name, g.id))} />
      </div>
    </div>
  );
}

import type { ReactNode } from "react";

/**
 * Charts of the dashboards (task F2), drawn with plain elements: no chart library, nothing that
 * runs in the browser, and flex layouts that mirror on their own in right-to-left Arabic. Charts
 * are decoration for sighted readers (hidden from screen readers); every value is also written as
 * text in a table with a caption right below.
 */

const cell = "border-b border-line px-2 py-1 text-start";

/** A figure with its label (used for the value column of tables). */
export interface ChartValue {
  /** Numeric value for the bar; null when there is none (shown as a gap). */
  value: number | null;
  /** The value as the reader reads it (their digits, %). */
  text: string;
}

/** One row of a bar list. */
export interface BarRow {
  key: string;
  label: string;
  value: ChartValue;
}

/** Horizontal bars with the value beside each, as a table. */
export function BarList({ caption, rows, max, valueHeading, labelHeading, empty, testId }: {
  caption: string;
  rows: BarRow[];
  /** Full width of a bar; the largest value by default (100 for percentages). */
  max?: number;
  valueHeading: string;
  labelHeading: string;
  empty: string;
  testId?: string;
}) {
  const top = max ?? Math.max(1, ...rows.map((r) => r.value.value ?? 0));
  return (
    <section className="space-y-2" data-testid={testId}>
      {rows.length === 0 ? (
        <>
          <h3 className="font-semibold">{caption}</h3>
          <p className="text-ink-muted">{empty}</p>
        </>
      ) : (
        <table className="w-full border-collapse text-sm">
          <caption className="pb-1 text-start font-semibold">{caption}</caption>
          <thead>
            <tr>
              <th scope="col" className={cell}>{labelHeading}</th>
              <th scope="col" className={`${cell} w-2/5`}><span className="sr-only">{valueHeading}</span></th>
              <th scope="col" className={`${cell} text-end`}>{valueHeading}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.key}>
                <th scope="row" className={`${cell} font-normal`}><bdi>{r.label}</bdi></th>
                <td className={cell}>
                  <span aria-hidden="true" className="block h-3 rounded-full bg-surface-muted">
                    <span className="block h-3 rounded-full bg-brand" style={{ width: `${Math.min(100, Math.max(0, ((r.value.value ?? 0) / top) * 100))}%` }} />
                  </span>
                </td>
                <td className={`${cell} text-end tabular-nums`}>{r.value.text}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

/** A series of a trend: its name, colour class and values (one per bucket). */
export interface TrendSeries {
  key: string;
  name: string;
  /** A background colour class, for example `bg-brand`. */
  colour: string;
  values: ChartValue[];
}

/**
 * Columns over time, one group per bucket and one column per series, with an optional target line
 * (percent charts). Buckets run from the start side, so the chart reads right to left in Arabic,
 * like the text. The table below gives every value.
 */
export function TrendChart({ caption, buckets, series, max, target, bucketHeading, empty, testId }: {
  caption: string;
  /** Labels of the buckets (dates in the reader's calendar). */
  buckets: string[];
  series: TrendSeries[];
  max?: number;
  /** A target value drawn as a line, with its text. */
  target?: { value: number; text: string };
  bucketHeading: string;
  empty: string;
  testId?: string;
}) {
  const values = series.flatMap((s) => s.values.map((v) => v.value ?? 0));
  const top = max ?? Math.max(1, ...values);
  const hasData = values.some((v) => v > 0);
  return (
    <section className="space-y-3" data-testid={testId}>
      <h3 className="font-semibold">{caption}</h3>
      {!hasData ? <p className="text-ink-muted">{empty}</p> : (
        <div aria-hidden="true" className="space-y-2">
          <div className="flex flex-wrap gap-4 text-xs">
            {series.map((s) => (
              <span key={s.key} className="inline-flex items-center gap-1"><span className={`inline-block h-3 w-3 rounded-sm ${s.colour}`} />{s.name}</span>
            ))}
            {target && <span className="inline-flex items-center gap-1"><span className="inline-block h-0.5 w-4 bg-warn" />{target.text}</span>}
          </div>
          <div className="relative overflow-x-auto" tabIndex={0}>
            <div className="relative flex h-40 min-w-full items-end gap-1 border-b border-line">
              {target && (
                <span className="pointer-events-none absolute inset-x-0 border-t-2 border-dashed border-warn" style={{ bottom: `${Math.min(100, (target.value / top) * 100)}%` }} />
              )}
              {buckets.map((bucket, i) => (
                <div key={`${bucket}-${i}`} className="flex h-full min-w-2 flex-1 items-end justify-center gap-px" title={bucket}>
                  {series.map((s) => {
                    const v = s.values[i]?.value;
                    return <span key={s.key} className={`block w-full max-w-4 rounded-t-sm ${s.colour}`} style={{ height: v === null || v === undefined ? 0 : `${Math.max(v > 0 ? 2 : 0, (v / top) * 100)}%` }} />;
                  })}
                </div>
              ))}
            </div>
            <div className="flex justify-between text-xs text-ink-muted">
              <span>{buckets[0]}</span>
              <span>{buckets[buckets.length - 1]}</span>
            </div>
          </div>
        </div>
      )}
      <details className="text-sm">
        <summary className="cursor-pointer font-semibold text-brand">{caption}: {bucketHeading}</summary>
        <div className="overflow-x-auto" tabIndex={0}>
          <table className="w-full border-collapse">
            <caption className="sr-only">{caption}</caption>
            <thead>
              <tr>
                <th scope="col" className={cell}>{bucketHeading}</th>
                {series.map((s) => <th key={s.key} scope="col" className={`${cell} text-end`}>{s.name}</th>)}
              </tr>
            </thead>
            <tbody>
              {buckets.map((bucket, i) => (
                <tr key={`${bucket}-${i}`}>
                  <th scope="row" className={`${cell} font-normal`}>{bucket}</th>
                  {series.map((s) => <td key={s.key} className={`${cell} text-end tabular-nums`}>{s.values[i]?.text ?? ""}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </section>
  );
}

/** A key figure with an optional target and whether it meets it. */
export function KpiTile({ label, value, note, state, testId, children }: {
  label: string;
  value: string;
  note?: string;
  /** Meets its target (ok), misses it (bad), or has none. */
  state?: "ok" | "bad";
  testId?: string;
  children?: ReactNode;
}) {
  const border = state === "ok" ? "border-ok" : state === "bad" ? "border-bad" : "border-line";
  return (
    <div className={`rounded-lg border-2 ${border} bg-surface p-3`} data-testid={testId}>
      <dt className="text-sm text-ink-muted">{label}</dt>
      <dd className="text-2xl font-bold tabular-nums">{value}</dd>
      {note && <dd className="text-sm">{note}</dd>}
      {children}
    </div>
  );
}

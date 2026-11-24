import { useTranslations } from "next-intl";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale } from "@/lib/incidents";
import { formatDuration, slaTone, type SlaClock } from "@/lib/sla";

/** An SLA clock's status as a labelled badge (text, not colour alone, carries the meaning). */
export function SlaBadge({ clock }: { clock: SlaClock }) {
  const t = useTranslations("sla.clock");
  return (
    <span className={`inline-block rounded-full border bg-surface px-2 py-0.5 text-xs font-semibold ${slaTone[clock.status]}`}>
      {t(`metrics.${clock.metric}`)}: {t(`status.${clock.status}`)}
    </span>
  );
}

/** The most urgent clock of a ticket for lists: breached first, then at risk, then the soonest due. */
export function mostUrgent(clocks: SlaClock[] | undefined): SlaClock | null {
  const rank = { Breached: 0, AtRisk: 1, Running: 2, Paused: 3, Met: 4, None: 5 } as const;
  const shown = (clocks ?? []).filter((c) => c.status !== "None" && !(c.status === "Met"));
  if (shown.length === 0) return null;
  return [...shown].sort((a, b) => rank[a.status] - rank[b.status] || (a.dueAt ?? "~").localeCompare(b.dueAt ?? "~"))[0]!;
}

/** One cell of an incident list: the most urgent clock and when it falls due. */
export function SlaCell({ clocks, display }: { clocks: SlaClock[] | undefined; display: DateDisplayOptions }) {
  const t = useTranslations("sla.clock");
  const clock = mostUrgent(clocks);
  if (!clock) return <span className="text-ink-muted">—</span>;
  return (
    <span className="space-y-1">
      <SlaBadge clock={clock} />
      {clock.dueAt && clock.status !== "Breached" && (
        <span className="block text-xs text-ink-muted">{t("due", { date: formatDateTime(new Date(clock.dueAt), display) })}</span>
      )}
    </span>
  );
}

/**
 * The response and resolution clocks of an incident (task B3): status, target, time used and when
 * the target falls due, on the business calendar that applies. Live timers come with B4.
 */
export function SlaClocks({ clocks, display }: { clocks: SlaClock[]; display: DateDisplayOptions }) {
  const t = useTranslations("sla.clock");
  const locale = display.locale as Locale;
  const date = (value: string | null) => (value ? formatDateTime(new Date(value), display) : null);
  const withTarget = clocks.filter((c) => c.targetMinutes !== null);
  return (
    <section aria-labelledby="sla-clocks-heading" className="space-y-3" data-testid="sla-clocks">
      <h2 id="sla-clocks-heading" className="text-xl font-bold">{t("title")}</h2>
      {withTarget.length === 0 ? (
        <p>{t("noPolicy")}</p>
      ) : (
        <dl className="grid gap-3 sm:grid-cols-2">
          {withTarget.map((clock) => {
            const policy = clock.policy ? inLanguage(clock.policy.name, locale) : null;
            const calendar = clock.calendar ? inLanguage(clock.calendar.name, locale) : null;
            return (
              <div key={clock.metric} className="space-y-2 rounded-lg border border-line bg-surface p-3">
                <dt className="flex flex-wrap items-center gap-2 font-semibold">
                  {t(`metrics.${clock.metric}`)} <SlaBadge clock={clock} />
                </dt>
                <dd className="space-y-1 text-sm">
                  <p>
                    {t("target", { target: formatDuration(clock.targetMinutes!, locale, display.digits) })} ·{" "}
                    {t("used", { used: formatDuration(clock.elapsedSeconds / 60, locale, display.digits), percent: clock.percent })}
                  </p>
                  <div
                    role="progressbar"
                    aria-label={t("progress", { metric: t(`metrics.${clock.metric}`) })}
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={Math.min(100, clock.percent)}
                    className="h-2 overflow-hidden rounded-full bg-surface-muted"
                  >
                    <div className={`h-full ${clock.status === "Breached" ? "bg-bad" : clock.status === "AtRisk" ? "bg-warn" : "bg-brand"}`}
                      style={{ inlineSize: `${Math.min(100, clock.percent)}%` }} />
                  </div>
                  {clock.dueAt && clock.state === "Running" && <p>{t("due", { date: date(clock.dueAt) ?? "" })}</p>}
                  {clock.breachedAt && <p className="text-bad">{t("breachedAt", { date: date(clock.breachedAt) ?? "" })}</p>}
                  {clock.state === "Stopped" && clock.stoppedAt && <p>{t("stoppedAt", { date: date(clock.stoppedAt) ?? "" })}</p>}
                  {clock.state === "Paused" && <p>{t("pausedHint")}</p>}
                  <p className="text-ink-muted">
                    {policy && <span lang={policy.lang} dir={policy.dir}>{t("policy", { name: policy.value })}</span>}
                    {" · "}
                    {calendar ? <span lang={calendar.lang} dir={calendar.dir}>{t("calendar", { name: calendar.value })}</span> : t("allDay")}
                  </p>
                </dd>
              </div>
            );
          })}
        </dl>
      )}
    </section>
  );
}

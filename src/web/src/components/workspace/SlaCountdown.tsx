"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { formatNumber, type DateDisplayOptions } from "@/lib/format";
import { mostUrgent } from "@/components/sla/SlaClocks";
import type { SlaClock, SlaStatus } from "@/lib/sla";
import { minutesLeft } from "@/lib/workspace";

const tone: Record<SlaStatus, string> = {
  None: "border-line text-ink-muted",
  Running: "border-brand text-brand",
  AtRisk: "border-2 border-warn text-warn",
  Paused: "border-line text-ink-muted",
  Met: "border-ok text-ok",
  Breached: "border-2 border-bad text-bad",
};

/** The current time, moving on every `everyMs` milliseconds while shown. */
export function useNow(everyMs = 30_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(timer);
  }, [everyMs]);
  return now;
}

/**
 * A live SLA timer (module 5): time left until a target falls due, counting down while shown, in
 * the colours of its status. Due times come from the business calendar (B3), so the wall-clock
 * time left is when the breach happens. A timer that runs out shows as breached at once; the
 * server confirms within seconds through live updates. Text, not colour alone, carries the meaning.
 */
export function SlaCountdown({ clock, display, now }: { clock: SlaClock; display: DateDisplayOptions; now: number }) {
  const t = useTranslations("workspace.sla");
  const tClock = useTranslations("sla.clock");
  const metric = tClock(`metrics.${clock.metric}`);
  const left = clock.dueAt && clock.state === "Running" ? minutesLeft(clock.dueAt, now) : null;
  const status: SlaStatus = left !== null && left < 0 && clock.status !== "Met" ? "Breached" : clock.status;
  const number = (n: number) => formatNumber(n, display.locale, display.digits);
  let text: string;
  if (clock.state === "Paused") {
    text = t("paused", { metric });
  } else if (clock.state === "Stopped") {
    text = t(status === "Breached" ? "stoppedLate" : "stopped", { metric });
  } else if (left === null) {
    text = tClock(`status.${status}`);
  } else {
    const minutes = Math.abs(left);
    const amount = minutes >= 60
      ? t("hoursMinutes", { hours: number(Math.floor(minutes / 60)), minutes: number(minutes % 60) })
      : t("minutes", { minutes: number(minutes) });
    text = left < 0 ? t("overdue", { metric, amount }) : t("left", { metric, amount });
  }
  return (
    <span className={`inline-flex items-center gap-1 rounded-full border bg-surface px-2 py-0.5 text-xs font-semibold ${tone[status]}`} data-sla-status={status}>
      <span aria-hidden="true">{status === "Breached" ? "!" : status === "AtRisk" ? "▲" : "●"}</span>
      {text}
    </span>
  );
}

/** The most urgent clock of a ticket as a live timer, or a dash. */
export function SlaCountdownCell({ clocks, display, now }: { clocks: SlaClock[] | undefined; display: DateDisplayOptions; now: number }) {
  const clock = mostUrgent(clocks);
  return clock ? <SlaCountdown clock={clock} display={display} now={now} /> : <span className="text-ink-muted">—</span>;
}

/** Both clocks of the open ticket as live timers. */
export function LiveSlaTimers({ clocks, display }: { clocks: SlaClock[]; display: DateDisplayOptions }) {
  const t = useTranslations("workspace.sla");
  const now = useNow();
  const shown = clocks.filter((c) => c.targetMinutes !== null);
  if (shown.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-2" role="group" aria-label={t("label")} data-testid="live-sla">
      {shown.map((clock) => (
        <SlaCountdown key={clock.metric} clock={clock} display={display} now={now} />
      ))}
    </div>
  );
}

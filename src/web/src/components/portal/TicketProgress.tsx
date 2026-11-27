"use client";

import { useId } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { Localized } from "@/components/directory/common";
import { StateBadge } from "@/components/incidents/StateBadge";
import { textOf } from "@/lib/directory";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import type { PortalStep, PortalTicket } from "@/lib/portal";

function stepName(step: PortalStep, locale: Locale, t: (key: string) => string): string {
  return step.key ? t(`steps.${step.key}`) : textOf(step.name, locale);
}

/**
 * A ticket in the portal: number, type, title, state, the team that has it and a progress bar with
 * its steps (the service's own steps for service requests). Text carries every value; the bar and
 * colours only repeat it.
 */
export function TicketProgressCard({ ticket, display }: { ticket: PortalTicket; display: DateDisplayOptions }) {
  const t = useTranslations("portal.tickets");
  const locale = useLocale() as Locale;
  const id = useId();
  const { steps, current } = ticket.progress;
  const percent = steps.length > 1 ? Math.round((current / (steps.length - 1)) * 100) : 100;
  const currentName = steps[current] ? stepName(steps[current], locale, t) : "";
  return (
    <article aria-labelledby={`${id}-title`} className="space-y-3 rounded-lg border border-line p-4" data-testid="portal-ticket">
      <header className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 space-y-1">
          <p className="text-sm text-ink-muted">
            <span className="font-mono" dir="ltr">{ticket.number}</span>
            <span className="ms-2">{ticket.recordType ? <Localized text={ticket.recordType.name} locale={locale} /> : t(`types.${ticket.type}`)}</span>
          </p>
          <h3 id={`${id}-title`} className="text-lg font-semibold">
            <Link href={ticket.path} className="text-brand underline-offset-4 hover:underline" dir="auto">{ticket.title}</Link>
          </h3>
          {ticket.service && <p className="text-sm"><Localized text={ticket.service.name} locale={locale} /></p>}
        </div>
        <StateBadge state={ticket.state} />
      </header>
      <div role="progressbar" aria-labelledby={`${id}-title`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}
        aria-valuetext={t("stepOf", { current: current + 1, total: steps.length, name: currentName })}
        className="h-2 w-full overflow-hidden rounded-full bg-surface-muted">
        <div className="h-full bg-brand" style={{ width: `${percent}%` }} />
      </div>
      <ol className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
        {steps.map((step, i) => (
          <li key={i} aria-current={i === current ? "step" : undefined} className={i === current ? "font-semibold" : i < current ? "" : "text-ink-muted"}>
            <span className="sr-only">{i < current ? t("done") : i === current ? t("current") : t("upcoming")}: </span>
            <span aria-hidden="true" className="me-1">{i < current ? "✓" : i === current ? "●" : "○"}</span>
            {stepName(step, locale, t)}
          </li>
        ))}
      </ol>
      <p className="text-sm text-ink-muted">
        {ticket.team ? t("withTeam", { team: textOf(ticket.team.name, locale) }) : t(ticket.open ? "noTeamYet" : "finished")}
        <span className="ms-2">{t("updated", { when: formatDateTime(new Date(ticket.updatedAt), display) })}</span>
      </p>
    </article>
  );
}

/** A list of portal tickets. */
export function TicketProgressList({ tickets, display }: { tickets: PortalTicket[]; display: DateDisplayOptions }) {
  return (
    <ul className="grid gap-3 md:grid-cols-2">
      {tickets.map((ticket) => <li key={ticket.id}><TicketProgressCard ticket={ticket} display={display} /></li>)}
    </ul>
  );
}

import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, levelName, reasonName, type Incident, type IncidentSettings, type Locale } from "@/lib/incidents";
import { MajorBadge, StateBadge } from "./StateBadge";

/**
 * The facts of an incident as a description list. Fields the viewer may not see are missing from
 * the API's answer and are left out here too.
 */
export function IncidentFacts({ incident, settings, display, basePath }: {
  incident: Incident;
  settings: IncidentSettings | null;
  display: DateDisplayOptions;
  basePath: string;
}) {
  const t = useTranslations("incidents");
  const locale = display.locale as Locale;
  const matrix = settings?.priorityMatrix;
  const date = (value: string | null) => (value ? formatDateTime(new Date(value), display) : null);
  const facts: { label: string; value: React.ReactNode }[] = [
    { label: t("fields.state"), value: <StateBadge state={incident.state} /> },
  ];
  if (incident.priority !== undefined) facts.push({ label: t("fields.priority"), value: levelName(matrix?.priorities ?? [], incident.priority, locale) });
  if (incident.impact !== undefined) facts.push({ label: t("fields.impact"), value: levelName(matrix?.impacts ?? [], incident.impact, locale) });
  if (incident.urgency !== undefined) facts.push({ label: t("fields.urgency"), value: levelName(matrix?.urgencies ?? [], incident.urgency, locale) });
  if (incident.requester) facts.push({ label: t("fields.requester"), value: incident.requester.displayName ?? "—" });
  if (incident.assignee !== undefined) facts.push({ label: t("fields.assignee"), value: incident.assignee?.displayName ?? t("fields.unassigned") });
  const named = (text: { en: string | null; ar: string | null }) => {
    const shown = inLanguage(text, locale);
    return <span lang={shown.lang} dir={shown.dir}>{shown.value}</span>;
  };
  if (incident.group !== undefined) facts.push({ label: t("fields.group"), value: incident.group ? named(incident.group.name) : t("fields.noGroup") });
  if (incident.category !== undefined) facts.push({ label: t("fields.category"), value: incident.category ? named(incident.category.path) : t("fields.notSet") });
  if (incident.location !== undefined) facts.push({ label: t("fields.location"), value: incident.location ? named(incident.location.path) : t("fields.notSet") });
  if (incident.routingRule) facts.push({ label: t("fields.routingRule"), value: named(incident.routingRule.name) });
  if (incident.channel) facts.push({ label: t("fields.channel"), value: t(`channels.${incident.channel}`) });
  facts.push({ label: t("fields.created"), value: date(incident.createdAt) });
  facts.push({ label: t("fields.updated"), value: date(incident.updatedAt) });
  if (incident.onHoldReason) facts.push({ label: t("fields.onHoldReason"), value: reasonName(settings?.onHoldReasons ?? [], incident.onHoldReason, locale) });
  if (incident.resolvedAt) facts.push({ label: t("fields.resolved"), value: date(incident.resolvedAt) });
  if (incident.closedAt) facts.push({ label: t("fields.closed"), value: date(incident.closedAt) });
  if (incident.reopenCount > 0) facts.push({ label: t("fields.reopenCount"), value: incident.reopenCount });
  if (incident.parent) {
    facts.push({
      label: t("fields.parent"),
      value: (
        <Link href={`${basePath}/${incident.parent.id}`} className="font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">
          {incident.parent.number}
        </Link>
      ),
    });
  }
  if (incident.followUpOf) {
    facts.push({
      label: t("fields.followUpOf"),
      value: (
        <Link href={`${basePath}/${incident.followUpOf.id}`} className="font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">
          {incident.followUpOf.number}
        </Link>
      ),
    });
  }

  return (
    <div className="space-y-4">
      {incident.isMajor && (
        <p className="flex items-center gap-2">
          <MajorBadge />
          {incident.majorDeclaredAt && <span className="text-sm text-ink-muted">{t("fields.majorSince", { date: date(incident.majorDeclaredAt) ?? "" })}</span>}
        </p>
      )}
      <dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {facts.map((fact) => (
          <div key={fact.label} className="rounded-lg border border-line bg-surface p-3">
            <dt className="text-sm text-ink-muted">{fact.label}</dt>
            <dd className="font-semibold">{fact.value}</dd>
          </div>
        ))}
      </dl>
      <section aria-labelledby="incident-description" className="space-y-1">
        <h2 id="incident-description" className="text-lg font-bold">
          {t("fields.description")}
        </h2>
        <p className="whitespace-pre-wrap" dir="auto">
          {incident.description ?? t("fields.noDescription")}
        </p>
      </section>
      {incident.resolutionNotes && (
        <section aria-labelledby="incident-resolution" className="space-y-1">
          <h2 id="incident-resolution" className="text-lg font-bold">
            {t("fields.resolutionNotes")}
          </h2>
          <p className="whitespace-pre-wrap" dir="auto">
            {incident.resolutionNotes}
          </p>
        </section>
      )}
      {incident.children && incident.children.length > 0 && (
        <section aria-labelledby="incident-children" className="space-y-1">
          <h2 id="incident-children" className="text-lg font-bold">
            {t("fields.children")}
          </h2>
          <ul className="space-y-1">
            {incident.children.map((child) => (
              <li key={child.id} className="flex flex-wrap items-center gap-2">
                <Link href={`${basePath}/${child.id}`} className="font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">
                  {child.number}
                </Link>
                <span dir="auto">{child.title}</span>
                <StateBadge state={child.state} />
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { levelName, type Incident, type IncidentSettings, type Locale } from "@/lib/incidents";
import { SlaCell } from "@/components/sla/SlaClocks";
import { MajorBadge, StateBadge } from "./StateBadge";

export interface IncidentTableProps {
  incidents: Incident[];
  settings: IncidentSettings | null;
  /** Where each incident opens: "/incidents" (staff) or "/my-incidents" (requesters). */
  basePath: string;
  /** Show requester and assignee columns (staff). */
  staff: boolean;
  display: DateDisplayOptions;
}

/** A list of incidents as a table: number, title, state, priority (and people for staff), last update. */
export function IncidentTable({ incidents, settings, basePath, staff, display }: IncidentTableProps) {
  const t = useTranslations("incidents");
  const locale = display.locale as Locale;
  const priorities = settings?.priorityMatrix.priorities ?? [];

  return (
    <div className="overflow-x-auto rounded-lg border border-line">
      <table className="w-full border-collapse text-start text-sm">
        <thead className="bg-surface-muted">
          <tr>
            <th scope="col" className="px-3 py-2 text-start">{t("columns.number")}</th>
            <th scope="col" className="px-3 py-2 text-start">{t("columns.title")}</th>
            <th scope="col" className="px-3 py-2 text-start">{t("columns.state")}</th>
            <th scope="col" className="px-3 py-2 text-start">{t("columns.priority")}</th>
            {staff && <th scope="col" className="px-3 py-2 text-start">{t("columns.requester")}</th>}
            {staff && <th scope="col" className="px-3 py-2 text-start">{t("columns.assignee")}</th>}
            {staff && <th scope="col" className="px-3 py-2 text-start">{t("columns.sla")}</th>}
            <th scope="col" className="px-3 py-2 text-start">{t("columns.updated")}</th>
          </tr>
        </thead>
        <tbody>
          {incidents.map((incident) => (
            <tr key={incident.id} className="border-t border-line">
              <td className="px-3 py-2 font-mono" dir="ltr">
                <Link href={`${basePath}/${incident.id}`} className="font-semibold text-brand underline-offset-4 hover:underline">
                  {incident.number}
                </Link>
              </td>
              <td className="px-3 py-2">
                <span dir="auto">{incident.title}</span> {incident.isMajor && <MajorBadge />}
              </td>
              <td className="px-3 py-2">
                <StateBadge state={incident.state} />
              </td>
              <td className="px-3 py-2">{levelName(priorities, incident.priority, locale)}</td>
              {staff && <td className="px-3 py-2">{incident.requester?.displayName ?? "—"}</td>}
              {staff && <td className="px-3 py-2">{incident.assignee?.displayName ?? t("fields.unassigned")}</td>}
              {staff && (
                <td className="px-3 py-2">
                  <SlaCell clocks={incident.sla} display={display} />
                </td>
              )}
              <td className="px-3 py-2">{formatDateTime(new Date(incident.updatedAt), display)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Previous / next page links that keep the filters. */
export function Pagination({ page, pageSize, totalCount, query, basePath }: {
  page: number;
  pageSize: number;
  totalCount: number;
  query: Record<string, string>;
  basePath: string;
}) {
  const t = useTranslations("incidents.pagination");
  const pages = Math.max(1, Math.ceil(totalCount / pageSize));
  if (pages <= 1) {
    return null;
  }
  const href = (to: number) => `${basePath}?${new URLSearchParams({ ...query, page: String(to) })}`;
  const linkClass = "rounded-md border border-line px-3 py-1.5 text-sm font-semibold text-brand hover:bg-surface-muted";
  return (
    <nav aria-label={t("label")} className="flex items-center gap-3">
      {page > 1 && (
        <Link href={href(page - 1)} className={linkClass}>
          {t("previous")}
        </Link>
      )}
      <span className="text-sm">{t("page", { page, pages })}</span>
      {page < pages && (
        <Link href={href(page + 1)} className={linkClass}>
          {t("next")}
        </Link>
      )}
    </nav>
  );
}

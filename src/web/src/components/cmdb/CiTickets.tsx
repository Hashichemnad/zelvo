import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import type { CiTicket } from "@/lib/cmdb";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";

/** Where staff open a ticket of each record type. */
function ticketHref(ticket: CiTicket): string {
  if (ticket.type === "Incident") return `/incidents/${ticket.id}`;
  if (ticket.type === "ServiceRequest") return `/requests/${ticket.id}`;
  return `/customer-voice/${ticket.id}`;
}

/** The tickets a configuration item is on (task G1), newest first, of the record types the viewer may read. */
export function CiTickets({ tickets, display }: { tickets: CiTicket[]; display: DateDisplayOptions }) {
  const t = useTranslations("cmdb.tickets");
  const tStates = useTranslations("incidents.states");
  return (
    <section aria-labelledby="ci-tickets" className="space-y-3">
      <h2 id="ci-tickets" className="text-xl font-bold">{t("heading")}</h2>
      {tickets.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("heading")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("number")}</th>
                <th scope="col" className={cellClass}>{t("title")}</th>
                <th scope="col" className={cellClass}>{t("type")}</th>
                <th scope="col" className={cellClass}>{t("state")}</th>
                <th scope="col" className={cellClass}>{t("linked")}</th>
              </tr>
            </thead>
            <tbody>
              {tickets.map((ticket) => (
                <tr key={ticket.id}>
                  <td className={cellClass}>
                    <Link href={ticketHref(ticket)} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{ticket.number}</Link>
                  </td>
                  <td className={cellClass} dir="auto">{ticket.title}</td>
                  <td className={cellClass}>{t.has(`types.${ticket.type}`) ? t(`types.${ticket.type}`) : ticket.type}</td>
                  <td className={cellClass}>{tStates.has(ticket.state) ? tStates(ticket.state) : ticket.state}</td>
                  <td className={cellClass}>{formatDateTime(new Date(ticket.linkedAt), display)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

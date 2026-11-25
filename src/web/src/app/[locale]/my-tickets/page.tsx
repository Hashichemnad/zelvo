import { getTranslations, setRequestLocale } from "next-intl/server";
import { Pagination } from "@/components/incidents/IncidentTable";
import { TicketProgressList } from "@/components/portal/TicketProgress";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { portalTicketTypes, type PortalTicketPage } from "@/lib/portal";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-tickets">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "portal.tickets" });
  return { title: t("title") };
}

/**
 * "My tickets" (module 4, task D2): every ticket the person asked for, of every record type, with
 * where it is and which team has it. My approvals come with task C3 (decided 2026-11-24).
 */
export default async function MyTicketsPage({ params, searchParams }: PageProps<"/[locale]/my-tickets">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-tickets`);
  const t = await getTranslations({ locale, namespace: "portal.tickets" });
  const search = await searchParams;
  const type = typeof search.type === "string" && (portalTicketTypes as string[]).includes(search.type) ? search.type : "";
  const open = search.open === "false" ? "false" : "true";
  const requested = Number(search.page);
  const page = Number.isInteger(requested) && requested > 0 && requested < 10_000 ? requested : 1;
  const filter: Record<string, string> = { open, ...(type ? { type } : {}) };
  const [tickets, format] = await Promise.all([
    getJsonAs<PortalTicketPage>(`/api/v1/me/tickets?${new URLSearchParams({ ...filter, page: String(page), pageSize: "20" })}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const input = "rounded-md border border-line bg-surface px-3 py-2";
  return (
    <section aria-labelledby="my-tickets-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="my-tickets-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        <Link href="/" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("new")}</Link>
      </div>
      <p className="max-w-2xl">{t("intro")}</p>
      <form method="get" className="flex flex-wrap items-end gap-3" aria-label={t("filters")}>
        <div className="space-y-1">
          <label htmlFor="tickets-open" className="block text-sm">{t("show")}</label>
          <select id="tickets-open" name="open" defaultValue={open} className={input}>
            <option value="true">{t("openOnly")}</option>
            <option value="false">{t("all")}</option>
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor="tickets-type" className="block text-sm">{t("type")}</label>
          <select id="tickets-type" name="type" defaultValue={type} className={input}>
            <option value="">{t("anyType")}</option>
            {portalTicketTypes.map((v) => <option key={v} value={v}>{t(`types.${v}`)}</option>)}
          </select>
        </div>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("apply")}</button>
      </form>
      {!tickets ? <p role="alert">{t("unavailable")}</p> : tickets.items.length === 0 ? <p>{t(open === "true" ? "emptyOpen" : "empty")}</p> : (
        <>
          <TicketProgressList tickets={tickets.items} display={{ locale, ...format }} />
          <Pagination page={tickets.page} pageSize={tickets.pageSize} totalCount={tickets.totalCount} query={filter} basePath="/my-tickets" />
        </>
      )}
      <p className="text-sm text-ink-muted">{t("approvalsLater")}</p>
    </section>
  );
}

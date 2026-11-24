import { getTranslations, setRequestLocale } from "next-intl/server";
import { RequestTable } from "@/components/catalog/ServiceRequests";
import { Pagination } from "@/components/incidents/IncidentTable";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { ticketStates } from "@/lib/incidents";
import type { ServiceRequestPage } from "@/lib/catalog";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/requests">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.requests" });
  return { title: t("title") };
}

/** Service requests for staff (requests.read): filter by open, state, assignee and group. */
export default async function RequestsPage({ params, searchParams }: PageProps<"/[locale]/requests">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/requests`);
  const t = await getTranslations({ locale, namespace: "catalog.requests" });
  const tStates = await getTranslations({ locale, namespace: "incidents.states" });
  if (!user?.permissions.includes("requests.read")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const search = await searchParams;
  const pick = (key: string, allowed: readonly string[]) => (typeof search[key] === "string" && allowed.includes(search[key]) ? search[key] : "");
  const state = pick("state", ticketStates);
  const assignee = pick("assignee", ["me", "none"]);
  const group = pick("group", ["mine", "none"]);
  const open = search.open === "false" ? "false" : "true";
  const requested = Number(search.page);
  const page = Number.isInteger(requested) && requested > 0 && requested < 10_000 ? requested : 1;
  const filter: Record<string, string> = { ...(state ? { state } : { open }), ...(assignee ? { assignee } : {}), ...(group ? { group } : {}) };
  const [requests, format] = await Promise.all([
    getJsonAs<ServiceRequestPage>(`/api/v1/service-requests?${new URLSearchParams({ ...filter, page: String(page), pageSize: "25" })}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const input = "rounded-md border border-line bg-surface px-3 py-2";
  return (
    <section aria-labelledby="requests-heading" className="space-y-6">
      <h1 id="requests-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <form method="get" className="flex flex-wrap items-end gap-3" aria-label={t("filters")}>
        <div className="space-y-1">
          <label htmlFor="requests-state" className="block text-sm">{t("state")}</label>
          <select id="requests-state" name="state" defaultValue={state} className={input}>
            <option value="">{t("anyState")}</option>
            {ticketStates.map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor="requests-open" className="block text-sm">{t("openFilter")}</label>
          <select id="requests-open" name="open" defaultValue={open} className={input}>
            <option value="true">{t("openOnly")}</option>
            <option value="false">{t("allRequests")}</option>
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor="requests-assignee" className="block text-sm">{t("assignee")}</label>
          <select id="requests-assignee" name="assignee" defaultValue={assignee} className={input}>
            <option value="">{t("anyone")}</option>
            <option value="me">{t("me")}</option>
            <option value="none">{t("nobody")}</option>
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor="requests-group" className="block text-sm">{t("group")}</label>
          <select id="requests-group" name="group" defaultValue={group} className={input}>
            <option value="">{t("anyGroup")}</option>
            <option value="mine">{t("myGroups")}</option>
            <option value="none">{t("noGroup")}</option>
          </select>
        </div>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("applyFilters")}</button>
      </form>
      {!requests ? <p role="alert">{t("unavailable")}</p> : requests.items.length === 0 ? <p>{t("empty")}</p> : (
        <>
          <RequestTable requests={requests.items} basePath="/requests" staff display={{ locale, ...format }} />
          <Pagination page={requests.page} pageSize={requests.pageSize} totalCount={requests.totalCount} query={filter} basePath="/requests" />
        </>
      )}
    </section>
  );
}

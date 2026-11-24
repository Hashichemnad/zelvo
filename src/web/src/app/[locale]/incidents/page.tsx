import { getTranslations, setRequestLocale } from "next-intl/server";
import { IncidentTable, Pagination } from "@/components/incidents/IncidentTable";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { GroupSummary } from "@/lib/directory";
import { inLanguage, levelName, ticketStates, type IncidentPage, type IncidentSettings, type Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/incidents">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "incidents" });
  return { title: t("title") };
}

const filterKeys = ["state", "open", "priority", "assignee", "group", "major"] as const;
const selectClass =
  "rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

/** Incidents for staff (incidents.read): every incident of the organisation, with filters. */
export default async function IncidentsPage({ params, searchParams }: PageProps<"/[locale]/incidents">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/incidents`);
  const t = await getTranslations({ locale, namespace: "incidents" });
  const raw = await searchParams;
  const query: Record<string, string> = {};
  for (const key of [...filterKeys, "page"]) {
    const value = raw[key];
    if (typeof value === "string" && value.length > 0 && value.length < 40) query[key] = value;
  }

  if (!user?.permissions.includes("incidents.read")) {
    return (
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>
        <Link href="/my-incidents" className="font-semibold text-brand underline">
          {t("nav.myIncidents")}
        </Link>
      </section>
    );
  }

  const [page, settings, format, groups] = await Promise.all([
    getJsonAs<IncidentPage>(`/api/v1/incidents?${new URLSearchParams({ ...query, pageSize: "25" })}`, session.data.accessToken, tenant),
    getJsonAs<IncidentSettings>("/api/v1/settings/incidents", session.data.accessToken, tenant),
    getDisplayFormat(),
    user.permissions.includes("directory.read")
      ? getJsonAs<GroupSummary[]>("/api/v1/groups", session.data.accessToken, tenant)
      : Promise.resolve(null),
  ]);
  const display = { locale, ...format };
  const filters = Object.fromEntries(Object.entries(query).filter(([key]) => key !== "page"));

  return (
    <section aria-labelledby="incidents-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="incidents-heading" className="text-3xl font-bold text-brand">
          {t("title")}
        </h1>
        {user.permissions.includes("incidents.create") && (
          <Link href="/incidents/new" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">
            {t("logIncident")}
          </Link>
        )}
      </div>
      <p>{t("intro")}</p>
      <form method="get" aria-label={t("filters.label")} className="flex flex-wrap items-end gap-3 rounded-lg border border-line p-3">
        <label className="space-y-1 text-sm">
          <span className="block font-semibold">{t("filters.state")}</span>
          <select name="state" defaultValue={query.state ?? ""} className={selectClass}>
            <option value="">{t("filters.anyState")}</option>
            {ticketStates.map((state) => (
              <option key={state} value={state}>
                {t(`states.${state}`)}
              </option>
            ))}
          </select>
        </label>
        <label className="space-y-1 text-sm">
          <span className="block font-semibold">{t("filters.priority")}</span>
          <select name="priority" defaultValue={query.priority ?? ""} className={selectClass}>
            <option value="">{t("filters.anyPriority")}</option>
            {settings?.priorityMatrix.priorities.map((p) => (
              <option key={p.level} value={p.level}>
                {levelName(settings.priorityMatrix.priorities, p.level, locale as Locale)}
              </option>
            ))}
          </select>
        </label>
        <label className="space-y-1 text-sm">
          <span className="block font-semibold">{t("filters.assignee")}</span>
          <select name="assignee" defaultValue={query.assignee ?? ""} className={selectClass}>
            <option value="">{t("filters.anyone")}</option>
            <option value="me">{t("filters.me")}</option>
            <option value="none">{t("filters.nobody")}</option>
          </select>
        </label>
        <label className="space-y-1 text-sm">
          <span className="block font-semibold">{t("filters.group")}</span>
          <select name="group" defaultValue={query.group ?? ""} className={selectClass}>
            <option value="">{t("filters.anyGroup")}</option>
            <option value="mine">{t("filters.myGroups")}</option>
            <option value="none">{t("filters.noGroup")}</option>
            {groups?.map((g) => (
              <option key={g.id} value={g.id}>
                {inLanguage(g.name, locale as Locale).value}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2 pb-2 text-sm">
          <input type="checkbox" name="open" value="true" defaultChecked={query.open === "true"} />
          {t("filters.openOnly")}
        </label>
        <label className="flex items-center gap-2 pb-2 text-sm">
          <input type="checkbox" name="major" value="true" defaultChecked={query.major === "true"} />
          {t("filters.major")}
        </label>
        <button type="submit" className="rounded-md border border-line px-3 py-2 text-sm font-semibold hover:bg-surface-muted">
          {t("filters.apply")}
        </button>
      </form>
      {!page ? (
        <p role="alert">{t("unavailable")}</p>
      ) : page.items.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <>
          <p className="text-sm text-ink-muted" aria-live="polite">
            {t("count", { count: page.totalCount })}
          </p>
          <IncidentTable incidents={page.items} settings={settings} basePath="/incidents" staff display={display} />
          <Pagination page={page.page} pageSize={page.pageSize} totalCount={page.totalCount} query={filters} basePath="/incidents" />
        </>
      )}
    </section>
  );
}

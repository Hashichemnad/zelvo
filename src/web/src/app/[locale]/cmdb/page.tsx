import { getTranslations, setRequestLocale } from "next-intl/server";
import { CiStatusBadge, FamilyLabel } from "@/components/cmdb/common";
import { Localized } from "@/components/directory/common";
import { Pagination } from "@/components/incidents/IncidentTable";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { ciStatuses, textIn, type CiClass, type CiPage } from "@/lib/cmdb";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { selectableNodes } from "@/lib/directory";
import { isId } from "@/lib/incident-pages";
import type { Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/cmdb">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "cmdb.list" });
  return { title: t("title") };
}

const selectClass =
  "block max-w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";
// A text box without a width is as wide as 20 average characters of the font, wider than a 320 px
// screen with a wide brand font (WCAG 1.4.10 reflow; as in Settings → Users).
const textClass = `${selectClass} w-64`;
const labelClass = "max-w-full space-y-1 text-sm";

/** Configuration items for staff (task G1, cmdb.read): search and filters by class, status, group and location. */
export default async function CmdbPage({ params, searchParams }: PageProps<"/[locale]/cmdb">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/cmdb`);
  const [t, tStatuses] = await Promise.all([
    getTranslations({ locale, namespace: "cmdb.list" }),
    getTranslations({ locale, namespace: "cmdb.statuses" }),
  ]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("cmdb.read")) {
    return (
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>
      </section>
    );
  }

  const raw = await searchParams;
  const value = (key: string) => (typeof raw[key] === "string" ? (raw[key] as string) : "");
  const query: Record<string, string> = {};
  const q = value("q").slice(0, 200);
  if (q) query.q = q;
  for (const key of ["classId", "groupId", "locationId"]) if (isId(value(key))) query[key] = value(key);
  if ((ciStatuses as readonly string[]).includes(value("status"))) query.status = value("status");
  if (value("includeRetired") === "true") query.includeRetired = "true";
  const pageNumber = Number(value("page"));
  const page = Number.isInteger(pageNumber) && pageNumber > 0 && pageNumber < 10000 ? pageNumber : 1;

  const token = session.data.accessToken;
  const [items, classes, choices] = await Promise.all([
    getJsonAs<CiPage>(`/api/v1/cmdb/items?${new URLSearchParams({ ...query, page: String(page), pageSize: "25" })}`, token, tenant),
    getJsonAs<CiClass[]>("/api/v1/cmdb/classes", token, tenant),
    loadDirectoryChoices(token, tenant, permissions),
  ]);
  const l = locale as Locale;

  return (
    <section aria-labelledby="cmdb-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="cmdb-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        {permissions.includes("cmdb.manage") && (
          <Link href="/cmdb/new" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("new")}</Link>
        )}
      </div>
      <p className="max-w-3xl">{t("intro")}</p>
      <form method="get" aria-label={t("filters")} className="flex min-w-0 flex-wrap items-end gap-3 rounded-lg border border-line p-3">
        <label className={labelClass}>
          <span className="block font-semibold">{t("search")}</span>
          <input type="search" name="q" defaultValue={q} maxLength={200} dir="auto" className={textClass} />
        </label>
        <label className={labelClass}>
          <span className="block font-semibold">{t("class")}</span>
          <select name="classId" defaultValue={query.classId ?? ""} className={`${selectClass} max-w-64`}>
            <option value="">{t("anyClass")}</option>
            {(classes ?? []).map((c) => <option key={c.id} value={c.id}>{textIn(c.path, l) || c.key}</option>)}
          </select>
        </label>
        <label className={labelClass}>
          <span className="block font-semibold">{t("status")}</span>
          <select name="status" defaultValue={query.status ?? ""} className={selectClass}>
            <option value="">{t("anyStatus")}</option>
            {ciStatuses.map((s) => <option key={s} value={s}>{tStatuses(s)}</option>)}
          </select>
        </label>
        {choices.groups && (
          <label className={labelClass}>
            <span className="block font-semibold">{t("group")}</span>
            <select name="groupId" defaultValue={query.groupId ?? ""} className={`${selectClass} max-w-64`}>
              <option value="">{t("anyGroup")}</option>
              {choices.groups.map((g) => <option key={g.id} value={g.id}>{textIn(g.name, l)}</option>)}
            </select>
          </label>
        )}
        <label className={labelClass}>
          <span className="block font-semibold">{t("location")}</span>
          <select name="locationId" defaultValue={query.locationId ?? ""} className={`${selectClass} max-w-64`}>
            <option value="">{t("anyLocation")}</option>
            {selectableNodes(choices.locations).map((n) => <option key={n.id} value={n.id}>{textIn(n.path, l) || textIn(n.name, l)}</option>)}
          </select>
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" name="includeRetired" value="true" defaultChecked={query.includeRetired === "true"} className="size-5 accent-brand" />
          {t("includeRetired")}
        </label>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("apply")}</button>
        <Link href="/cmdb" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">{t("clear")}</Link>
      </form>

      {!items ? (
        <p role="alert">{t("unavailable")}</p>
      ) : (
        <>
          <p role="status">{t("count", { count: items.totalCount })}</p>
          {items.items.length > 0 && (
            <div className="overflow-x-auto">
              <table className={tableClass} data-testid="ci-table">
                <caption className="sr-only">{t("title")}</caption>
                <thead>
                  <tr>
                    <th scope="col" className={cellClass}>{t("number")}</th>
                    <th scope="col" className={cellClass}>{t("name")}</th>
                    <th scope="col" className={cellClass}>{t("class")}</th>
                    <th scope="col" className={cellClass}>{t("status")}</th>
                    <th scope="col" className={cellClass}>{t("group")}</th>
                    <th scope="col" className={cellClass}>{t("location")}</th>
                  </tr>
                </thead>
                <tbody>
                  {items.items.map((item) => (
                    <tr key={item.id}>
                      <td className={cellClass}>
                        <Link href={`/cmdb/${item.id}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{item.number}</Link>
                      </td>
                      <td className={cellClass} dir="auto">{item.name}</td>
                      <td className={cellClass}>
                        <Localized text={item.class.path} locale={l} />
                        <span className="block text-xs text-ink-muted"><FamilyLabel family={item.class.family} /></span>
                      </td>
                      <td className={cellClass}><CiStatusBadge status={item.status} /></td>
                      <td className={cellClass}>{item.ownerGroup ? <Localized text={item.ownerGroup.name} locale={l} /> : "—"}</td>
                      <td className={cellClass}>{item.location ? <Localized text={item.location.path} locale={l} /> : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pagination page={items.page} pageSize={items.pageSize} totalCount={items.totalCount} query={query} basePath="/cmdb" />
        </>
      )}
    </section>
  );
}

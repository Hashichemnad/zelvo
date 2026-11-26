import { getTranslations, setRequestLocale } from "next-intl/server";
import { AddUserForm } from "@/components/directory/AddUserForm";
import { Pagination } from "@/components/incidents/IncidentTable";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { textOf, type Role, type UserPage } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/users">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.users" });
  return { title: t("title") };
}

const selectClass =
  "block max-w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";
// A text box without a width is as wide as 20 average characters of the font; with a wide brand
// font (IBM Plex Sans Arabic) that is wider than a 320 px screen (WCAG 1.4.10 reflow).
const textClass = `${selectClass} w-64`;
const labelClass = "max-w-full space-y-1 text-sm";
const cellClass = "border-b border-line px-2 py-2 text-start align-top";

/** Settings → Users (task B2): search, filter by role and status, add a user. */
export default async function UsersPage({ params, searchParams }: PageProps<"/[locale]/settings/users">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/users`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.users" }),
    getTranslations({ locale, namespace: "settings.sections.users" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!user?.permissions.includes("users.read")) {
    return <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>;
  }
  const raw = await searchParams;
  const query: Record<string, string> = {};
  for (const key of ["q", "role", "status", "page"]) {
    const value = raw[key];
    if (typeof value === "string" && value.length > 0 && value.length <= 100) query[key] = value;
  }
  const token = session.data.accessToken;
  const [page, roles] = await Promise.all([
    getJsonAs<UserPage>(`/api/v1/users?${new URLSearchParams({ ...query, pageSize: "25" })}`, token, tenant),
    user.permissions.includes("roles.manage") ? getJsonAs<Role[]>("/api/v1/roles", token, tenant) : Promise.resolve(null),
  ]);
  const roleName = (key: string) => {
    const role = roles?.find((r) => r.key === key);
    return role ? textOf(role.name, locale as Locale) : key;
  };
  const filters = Object.fromEntries(Object.entries(query).filter(([key]) => key !== "page"));

  return (
    <section aria-labelledby="users-heading" className="space-y-6">
      <h2 id="users-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      <p className="max-w-2xl">{t("intro")}</p>
      <form method="get" role="search" aria-label={t("filters")} className="flex flex-wrap items-end gap-3 rounded-lg border border-line p-3">
        <label className={labelClass}>
          <span className="block font-semibold">{t("search")}</span>
          <input type="search" name="q" defaultValue={query.q ?? ""} dir="auto" maxLength={100} className={textClass} />
        </label>
        <label className={labelClass}>
          <span className="block font-semibold">{t("role")}</span>
          {roles ? (
            <select name="role" defaultValue={query.role ?? ""} className={selectClass}>
              <option value="">{t("anyRole")}</option>
              {roles.map((r) => (
                <option key={r.key} value={r.key}>{textOf(r.name, locale as Locale)}</option>
              ))}
            </select>
          ) : (
            <input name="role" defaultValue={query.role ?? ""} dir="ltr" maxLength={50} className={textClass} />
          )}
        </label>
        <label className={labelClass}>
          <span className="block font-semibold">{t("status")}</span>
          <select name="status" defaultValue={query.status ?? ""} className={selectClass}>
            <option value="">{t("anyStatus")}</option>
            <option value="Active">{t("statuses.Active")}</option>
            <option value="Disabled">{t("statuses.Disabled")}</option>
          </select>
        </label>
        <button type="submit" className="rounded-md border border-line px-3 py-2 text-sm font-semibold hover:bg-surface-muted">{t("show")}</button>
      </form>
      {!page ? (
        <p role="alert">{tSettings("unavailable")}</p>
      ) : page.items.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <>
          <p className="text-sm text-ink-muted" aria-live="polite">{t("count", { count: page.totalCount })}</p>
          <div className="overflow-x-auto">
            <table className="w-full border-collapse text-start text-sm" data-testid="users-table">
              <caption className="sr-only">{t("caption")}</caption>
              <thead>
                <tr>
                  <th scope="col" className={cellClass}>{t("displayName")}</th>
                  <th scope="col" className={cellClass}>{t("email")}</th>
                  <th scope="col" className={cellClass}>{t("roles")}</th>
                  <th scope="col" className={cellClass}>{t("status")}</th>
                </tr>
              </thead>
              <tbody>
                {page.items.map((u) => (
                  <tr key={u.id}>
                    <td className={cellClass}>
                      <Link href={`/settings/users/${u.id}`} className="font-semibold text-brand underline-offset-4 hover:underline" dir="auto">
                        {u.displayName ?? t("noName")}
                      </Link>
                    </td>
                    <td className={cellClass} dir="ltr">{u.email ?? "—"}</td>
                    <td className={cellClass}>{u.roles?.map(roleName).join(locale === "ar" ? "، " : ", ") ?? "—"}</td>
                    <td className={cellClass}>{u.status ? t(`statuses.${u.status}`) : "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Pagination page={page.page} pageSize={page.pageSize} totalCount={page.totalCount} query={filters} basePath="/settings/users" />
        </>
      )}
      {user.permissions.includes("users.manage") && <AddUserForm roles={roles} csrfToken={session.data.csrfToken} />}
    </section>
  );
}

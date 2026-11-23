import { getTranslations, setRequestLocale } from "next-intl/server";
import { AddRoleForm } from "@/components/directory/AddRoleForm";
import { Localized } from "@/components/directory/common";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { Role } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/roles">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.roles" });
  return { title: t("title") };
}

const cellClass = "border-b border-line px-2 py-2 text-start align-top";

/** Settings → Roles (A2 data, B2 screens): the six personas and custom roles. */
export default async function RolesPage({ params }: PageProps<"/[locale]/settings/roles">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/roles`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.roles" }),
    getTranslations({ locale, namespace: "settings.sections.roles" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const roles = user?.permissions.includes("roles.manage") ? await getJsonAs<Role[]>("/api/v1/roles", session.data.accessToken, tenant) : null;

  return (
    <section aria-labelledby="roles-heading" className="space-y-4">
      <h2 id="roles-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!roles ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <table className="w-full border-collapse text-start text-sm">
            <caption className="sr-only">{t("caption")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("name")}</th>
                <th scope="col" className={cellClass}>{t("kind")}</th>
                <th scope="col" className={cellClass}>{t("requiresMfa")}</th>
                <th scope="col" className={cellClass}>{t("permissionCount")}</th>
              </tr>
            </thead>
            <tbody>
              {roles.map((role) => (
                <tr key={role.key}>
                  <td className={cellClass}>
                    <Link href={`/settings/roles/${role.key}`} className="font-semibold text-brand underline-offset-4 hover:underline">
                      <Localized text={role.name} locale={locale as "en" | "ar"} />
                    </Link>
                  </td>
                  <td className={cellClass}>{role.isSystem ? t("builtIn") : t("custom")}</td>
                  <td className={cellClass}>{role.requiresMfa ? t("yes") : t("no")}</td>
                  <td className={cellClass}>{role.permissions.length}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <AddRoleForm csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

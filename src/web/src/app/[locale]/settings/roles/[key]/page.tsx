import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { RoleEditForm } from "@/components/directory/RoleEditForm";
import { Localized } from "@/components/directory/common";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { PermissionCatalog, Role } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/roles/[key]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.roles" });
  return { title: t("title") };
}

/** One role: name, second factor, permissions and field rules. */
export default async function RolePage({ params }: PageProps<"/[locale]/settings/roles/[key]">) {
  const { locale, key } = await params;
  setRequestLocale(locale);
  if (!/^[a-z][a-z0-9-]{1,49}$/.test(key)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/roles/${key}`);
  const [t, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.roles" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!user?.permissions.includes("roles.manage")) {
    return <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [role, catalog] = await Promise.all([
    getJsonAs<Role>(`/api/v1/roles/${key}`, token, tenant),
    getJsonAs<PermissionCatalog>("/api/v1/permissions", token, tenant),
  ]);
  if (!role || !catalog) notFound();

  return (
    <section aria-labelledby="role-heading" className="space-y-4">
      <Link href="/settings/roles" className="font-semibold text-brand underline">{t("back")}</Link>
      <h2 id="role-heading" className="text-2xl font-bold">
        <Localized text={role.name} locale={locale as "en" | "ar"} />
      </h2>
      <RoleEditForm initial={role} catalog={catalog} csrfToken={session.data.csrfToken} />
    </section>
  );
}

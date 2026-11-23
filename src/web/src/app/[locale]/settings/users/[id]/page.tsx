import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { UserEditForm } from "@/components/directory/UserEditForm";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { DirectoryUser, Role, Skill } from "@/lib/directory";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/users/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.users" });
  return { title: t("title") };
}

/** One user: profile, roles, languages, skills, availability and groups. */
export default async function UserPage({ params }: PageProps<"/[locale]/settings/users/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/users/${id}`);
  const [t, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.users" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!user?.permissions.includes("users.read")) {
    return <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [account, roles, skills] = await Promise.all([
    getJsonAs<DirectoryUser>(`/api/v1/users/${id}`, token, tenant),
    user.permissions.includes("roles.manage") ? getJsonAs<Role[]>("/api/v1/roles", token, tenant) : Promise.resolve(null),
    user.permissions.includes("directory.read") ? getJsonAs<Skill[]>("/api/v1/skills?includeInactive=true", token, tenant) : Promise.resolve(null),
  ]);
  if (!account) notFound();

  return (
    <section aria-labelledby="user-heading" className="space-y-4">
      <Link href="/settings/users" className="font-semibold text-brand underline">{t("back")}</Link>
      <h2 id="user-heading" className="text-2xl font-bold" dir="auto">{account.displayName ?? t("noName")}</h2>
      <UserEditForm initial={account} roles={roles} skills={skills ?? []} csrfToken={session.data.csrfToken} canManage={user.permissions.includes("users.manage")} />
    </section>
  );
}

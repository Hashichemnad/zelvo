import { getTranslations, setRequestLocale } from "next-intl/server";
import { CiEditor } from "@/components/cmdb/CiEditor";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { CiClass } from "@/lib/cmdb";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/cmdb/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "cmdb.editor" });
  return { title: t("newHeading") };
}

/** A new configuration item (task G1, cmdb.manage). */
export default async function NewCiPage({ params }: PageProps<"/[locale]/cmdb/new">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/cmdb/new`);
  const [t, tList] = await Promise.all([
    getTranslations({ locale, namespace: "cmdb.editor" }),
    getTranslations({ locale, namespace: "cmdb.list" }),
  ]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("cmdb.manage")) {
    return <p role="alert">{user ? tList("noAccess") : tList("unavailable")}</p>;
  }
  const [classes, choices] = await Promise.all([
    getJsonAs<CiClass[]>("/api/v1/cmdb/classes", session.data.accessToken, tenant),
    loadDirectoryChoices(session.data.accessToken, tenant, permissions),
  ]);

  return (
    <section className="space-y-4">
      <Link href="/cmdb" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{tList("back")}</Link>
      <h1 className="text-3xl font-bold text-brand">{t("newHeading")}</h1>
      {!classes ? (
        <p role="alert">{tList("unavailable")}</p>
      ) : classes.length === 0 ? (
        <p>{t("noClasses")}</p>
      ) : (
        <CiEditor item={null} classes={classes} groups={choices.groups} locations={choices.locations} csrfToken={session.data.csrfToken} />
      )}
    </section>
  );
}

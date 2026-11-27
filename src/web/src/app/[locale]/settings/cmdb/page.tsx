import { getTranslations, setRequestLocale } from "next-intl/server";
import { ClassesEditor } from "@/components/cmdb/ClassesEditor";
import { CmdbPrefixForm } from "@/components/cmdb/CmdbPrefixForm";
import { RelationshipTypesEditor } from "@/components/cmdb/RelationshipTypesEditor";
import { getJsonAs } from "@/lib/api";
import type { CiClass, CiRelationshipType, CmdbSettings } from "@/lib/cmdb";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/cmdb">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.cmdb" });
  return { title: t("title") };
}

/** Settings → CMDB (task G1, module 18): classes and their attributes, relationship types and the number prefix. */
export default async function CmdbSettingsPage({ params }: PageProps<"/[locale]/settings/cmdb">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/cmdb`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "cmdb.settings" }),
    getTranslations({ locale, namespace: "settings.sections.cmdb" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const permissions = user?.permissions ?? [];
  const canRead = permissions.includes("cmdb.read") || permissions.includes("cmdb.manage");
  const token = session.data.accessToken;
  const [classes, types, settings] = canRead
    ? await Promise.all([
        getJsonAs<CiClass[]>("/api/v1/cmdb/classes", token, tenant),
        getJsonAs<CiRelationshipType[]>("/api/v1/cmdb/relationship-types", token, tenant),
        getJsonAs<CmdbSettings>("/api/v1/cmdb/settings", token, tenant),
      ])
    : [null, null, null];
  const canManage = permissions.includes("cmdb.manage");

  return (
    <section aria-labelledby="cmdb-settings-heading" className="space-y-8">
      <h2 id="cmdb-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!classes || !types || !settings ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <section aria-labelledby="cmdb-classes-heading" className="space-y-3">
            <h3 id="cmdb-classes-heading" className="text-xl font-semibold">{t("classesHeading")}</h3>
            <p className="max-w-2xl text-sm">{t("classesIntro")}</p>
            <ClassesEditor initial={classes} csrfToken={session.data.csrfToken} canManage={canManage} />
          </section>
          <section aria-labelledby="cmdb-types-heading" className="space-y-3">
            <h3 id="cmdb-types-heading" className="text-xl font-semibold">{t("typesHeading")}</h3>
            <p className="max-w-2xl text-sm">{t("typesIntro")}</p>
            <RelationshipTypesEditor initial={types} csrfToken={session.data.csrfToken} canManage={canManage} />
          </section>
          <section aria-labelledby="cmdb-numbers-heading" className="space-y-3">
            <h3 id="cmdb-numbers-heading" className="text-xl font-semibold">{t("numbersHeading")}</h3>
            <CmdbPrefixForm initial={settings} csrfToken={session.data.csrfToken} canManage={canManage} />
          </section>
        </>
      )}
    </section>
  );
}

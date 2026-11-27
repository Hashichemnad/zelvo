import { getTranslations, setRequestLocale } from "next-intl/server";
import { CiImportExport } from "@/components/cmdb/CiImportExport";
import { ClassesEditor } from "@/components/cmdb/ClassesEditor";
import { DiscoverySources, type ConnectorChoice } from "@/components/cmdb/DiscoverySources";
import { CmdbPrefixForm } from "@/components/cmdb/CmdbPrefixForm";
import { RelationshipTypesEditor } from "@/components/cmdb/RelationshipTypesEditor";
import { getJsonAs } from "@/lib/api";
import type { CiClass, CiRelationshipType, CmdbSettings, DiscoverySource } from "@/lib/cmdb";
import { getDisplayFormat } from "@/lib/display";
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
  // Task G2: discovery sources (people only) and import and export.
  const canDiscover = permissions.includes("cmdb.discovery.manage");
  const [sources, connectors, format] = await Promise.all([
    canDiscover ? getJsonAs<DiscoverySource[]>("/api/v1/cmdb/discovery/sources", token, tenant) : Promise.resolve(null),
    canDiscover && permissions.includes("connectors.manage")
      ? getJsonAs<ConnectorChoice[]>("/api/v1/connectors", token, tenant)
      : Promise.resolve(null),
    getDisplayFormat(),
  ]);
  const display = { locale, ...format };

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
          {sources && (
            <section aria-labelledby="cmdb-discovery-heading" className="space-y-3">
              <h3 id="cmdb-discovery-heading" className="text-xl font-semibold">{t("discoveryHeading")}</h3>
              <p className="max-w-2xl text-sm">{t("discoveryIntro")}</p>
              <DiscoverySources initial={sources} connectors={(connectors ?? []).map((c) => ({ id: c.id, name: c.name, kind: c.kind }))}
                csrfToken={session.data.csrfToken} display={display} />
            </section>
          )}
          <section aria-labelledby="cmdb-import-heading" className="space-y-3">
            <h3 id="cmdb-import-heading" className="text-xl font-semibold">{t("importHeading")}</h3>
            <p className="max-w-2xl text-sm">{t("importIntro")}</p>
            <CiImportExport csrfToken={session.data.csrfToken} canImport={canManage} locale={locale} digits={display.digits} />
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

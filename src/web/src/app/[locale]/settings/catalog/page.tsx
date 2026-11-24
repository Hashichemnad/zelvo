import { getTranslations, setRequestLocale } from "next-intl/server";
import { CatalogSettings } from "@/components/catalog/CatalogSettings";
import { getJsonAs } from "@/lib/api";
import type { Catalog, ServiceSummary, ServiceTemplate } from "@/lib/catalog";
import type { Department, GroupSummary } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/catalog">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.catalog" });
  return { title: t("title") };
}

/** Settings → Catalog (task C2, module 2): services, catalogs and their editors, the review queue and templates. */
export default async function CatalogSettingsPage({ params }: PageProps<"/[locale]/settings/catalog">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/catalog`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "catalog.settings" }),
    getTranslations({ locale, namespace: "settings.sections.catalog" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const token = session.data.accessToken;
  const permissions = user?.permissions ?? [];
  const get = <T,>(path: string) => getJsonAs<T>(path, token, tenant);
  const [services, catalogs, reviewQueue, templates, departments, groups] = await Promise.all([
    get<ServiceSummary[]>("/api/v1/catalog/services"),
    get<Catalog[]>("/api/v1/catalog/catalogs"),
    permissions.includes("catalog.publish") ? get<ServiceSummary[]>("/api/v1/catalog/services/review-queue") : Promise.resolve(null),
    get<ServiceTemplate[]>("/api/v1/catalog/templates"),
    get<Department[]>("/api/v1/departments"),
    permissions.includes("directory.read") ? get<GroupSummary[]>("/api/v1/groups") : Promise.resolve(null),
  ]);

  return (
    <section aria-labelledby="catalog-heading" className="space-y-6">
      <h2 id="catalog-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!services || !catalogs ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <CatalogSettings services={services} catalogs={catalogs} reviewQueue={reviewQueue} templates={templates} departments={departments ?? []}
            groups={groups ?? []} permissions={permissions} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

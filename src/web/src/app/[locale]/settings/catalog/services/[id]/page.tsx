import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { ServiceEditor } from "@/components/catalog/ServiceEditor";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { Catalog, ServiceDetail, ServiceSummary } from "@/lib/catalog";
import type { Department, GroupSummary, Role, TreeNode } from "@/lib/directory";
import type { FormSummary } from "@/lib/forms/types";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/catalog/services/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.editor" });
  return { title: t("title") };
}

/** The service editor (task C2): the whole card, its versions and lifecycle actions. */
export default async function ServiceEditorPage({ params }: PageProps<"/[locale]/settings/catalog/services/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/catalog/services/${id}`);
  const t = await getTranslations({ locale, namespace: "catalog.editor" });
  const token = session.data.accessToken;
  const permissions = user?.permissions ?? [];
  const get = <T,>(path: string) => getJsonAs<T>(path, token, tenant);
  const [detail, services, catalogs, categories, locations, departments, groups, roles, forms, format] = await Promise.all([
    get<ServiceDetail>(`/api/v1/catalog/services/${id}`),
    get<ServiceSummary[]>("/api/v1/catalog/services"),
    get<Catalog[]>("/api/v1/catalog/catalogs"),
    get<TreeNode[]>("/api/v1/categories"),
    get<TreeNode[]>("/api/v1/locations"),
    get<Department[]>("/api/v1/departments"),
    permissions.includes("directory.read") ? get<GroupSummary[]>("/api/v1/groups") : Promise.resolve(null),
    get<Role[]>("/api/v1/roles"),
    permissions.includes("forms.manage") ? get<FormSummary[]>("/api/v1/forms") : Promise.resolve(null),
    getDisplayFormat(),
  ]);
  return (
    <div className="space-y-4">
      <Link href="/settings/catalog" className="text-sm font-semibold text-brand underline">{t("back")}</Link>
      {!detail ? (
        <p role="alert">{t("notFound")}</p>
      ) : (
        <ServiceEditor initial={detail} csrfToken={session.data.csrfToken} display={{ locale, ...format }}
          choices={{ services: services ?? [], catalogs: catalogs ?? [], categories: categories ?? [], locations: locations ?? [], departments: departments ?? [], groups: groups ?? [], roles: roles ?? [], forms }} />
      )}
    </div>
  );
}

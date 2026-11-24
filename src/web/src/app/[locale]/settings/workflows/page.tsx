import { getTranslations, setRequestLocale } from "next-intl/server";
import { WorkflowRulesPanel } from "@/components/workflows/WorkflowRulesPanel";
import { getJsonAs } from "@/lib/api";
import type { ServiceSummary } from "@/lib/catalog";
import type { Department, GroupSummary, Role, TreeNode } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";
import type { WorkflowRule } from "@/lib/workflows";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/workflows">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.workflows" });
  return { title: t("title") };
}

/** Settings → Workflow rules (task C3, module 10): the no-code when / if / then builder. */
export default async function WorkflowsPage({ params }: PageProps<"/[locale]/settings/workflows">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/workflows`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "workflows" }),
    getTranslations({ locale, namespace: "settings.sections.workflows" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("workflows.manage") ?? false;
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [rules, services, categories, locations, groups, departments, roles] = allowed
    ? await Promise.all([
        get<WorkflowRule[]>("/api/v1/workflow-rules"),
        get<ServiceSummary[]>("/api/v1/catalog/services"),
        get<TreeNode[]>("/api/v1/categories"),
        get<TreeNode[]>("/api/v1/locations"),
        get<GroupSummary[]>("/api/v1/groups"),
        get<Department[]>("/api/v1/departments"),
        get<Role[]>("/api/v1/roles"),
      ])
    : [null, null, null, null, null, null, null];
  return (
    <section aria-labelledby="workflows-heading" className="space-y-4">
      <h2 id="workflows-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!rules ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <WorkflowRulesPanel initialRules={rules} csrfToken={session.data.csrfToken}
            choices={{ services: services ?? [], categories: categories ?? [], locations: locations ?? [], groups: groups ?? [], departments: departments ?? [], roles: roles ?? [] }} />
        </>
      )}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { BlackoutsPanel, ChangeSettingsForm, ChangeTemplatesPanel } from "@/components/changes/ChangeSettingsForm";
import { getJsonAs } from "@/lib/api";
import type { Blackout, ChangeSettings, ChangeTemplate } from "@/lib/changes";
import type { Department, GroupSummary, Role, TreeNode } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/changes">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.changes" });
  return { title: t("title") };
}

/**
 * Settings → Changes (task H3, changes.manage): the risk and impact questionnaire with its weights
 * and thresholds, the approval path of each risk level and of emergency changes, blackout periods
 * and the catalog of standard changes.
 */
export default async function ChangeSettingsPage({ params }: PageProps<"/[locale]/settings/changes">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/changes`);
  const [t, tSection] = await Promise.all([
    getTranslations({ locale, namespace: "changes.settings" }),
    getTranslations({ locale, namespace: "settings.sections.changes" }),
  ]);
  const canManage = (user?.permissions ?? []).includes("changes.manage");
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [settings, blackouts, templates, groups, roles, categories, departments] = canManage
    ? await Promise.all([
        get<ChangeSettings>("/api/v1/settings/changes"),
        get<Blackout[]>("/api/v1/change-blackouts?current=true"),
        get<ChangeTemplate[]>("/api/v1/change-templates?all=true"),
        get<GroupSummary[]>("/api/v1/groups"),
        get<Role[]>("/api/v1/roles"),
        get<TreeNode[]>("/api/v1/categories"),
        get<Department[]>("/api/v1/departments"),
      ])
    : [null, null, null, null, null, null, null];
  const csrfToken = session.data.csrfToken;

  return (
    <section aria-labelledby="change-settings-heading" className="space-y-6">
      <h2 id="change-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!settings ? <p role="alert">{t("noAccess")}</p> : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <ChangeSettingsForm initial={settings} csrfToken={csrfToken}
            choices={{ services: [], categories: categories ?? [], locations: [], groups: groups ?? [], departments: departments ?? [], roles: roles ?? [] }} />
          <BlackoutsPanel initial={blackouts ?? []} csrfToken={csrfToken} />
          <ChangeTemplatesPanel initial={templates ?? []} groups={groups ?? []} categories={categories ?? []} csrfToken={csrfToken} />
        </>
      )}
    </section>
  );
}

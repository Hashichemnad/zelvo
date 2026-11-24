import { getTranslations, setRequestLocale } from "next-intl/server";
import { AssignmentRulesPanel } from "@/components/directory/AssignmentRulesPanel";
import { getJsonAs } from "@/lib/api";
import type { AssignmentDefault, AssignmentRule, GroupSummary, Skill, TreeNode } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/assignment">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.assignment" });
  return { title: t("title") };
}

/** Settings → Assignment (module 1, task B2): ordered rules, the default and a rule tester. */
export default async function AssignmentPage({ params }: PageProps<"/[locale]/settings/assignment">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/assignment`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.assignment" }),
    getTranslations({ locale, namespace: "settings.sections.assignment" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("directory.read") ?? false;
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [rules, fallback, categories, locations, groups, skills] = allowed
    ? await Promise.all([
        get<AssignmentRule[]>("/api/v1/assignment-rules"),
        get<AssignmentDefault>("/api/v1/assignment-rules/default"),
        get<TreeNode[]>("/api/v1/categories?includeInactive=true"),
        get<TreeNode[]>("/api/v1/locations?includeInactive=true"),
        get<GroupSummary[]>("/api/v1/groups?includeInactive=true"),
        get<Skill[]>("/api/v1/skills?includeInactive=true"),
      ])
    : [null, null, null, null, null, null];

  return (
    <section aria-labelledby="assignment-heading" className="space-y-4">
      <h2 id="assignment-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!rules || !fallback ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <AssignmentRulesPanel
            initialRules={rules}
            initialDefault={fallback}
            choices={{ categories: categories ?? [], locations: locations ?? [], groups: groups ?? [], skills: skills ?? [] }}
            csrfToken={session.data.csrfToken}
            canManage={user!.permissions.includes("directory.manage")}
          />
        </>
      )}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { SkillsPanel } from "@/components/directory/SkillsPanel";
import { getJsonAs } from "@/lib/api";
import type { Skill } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/skills">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.skills" });
  return { title: t("title") };
}

/** Settings → Skills (task B2): what agents can do; round-robin rules can require a skill. */
export default async function SkillsPage({ params }: PageProps<"/[locale]/settings/skills">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/skills`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.skills" }),
    getTranslations({ locale, namespace: "settings.sections.skills" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const skills = user?.permissions.includes("directory.read")
    ? await getJsonAs<Skill[]>("/api/v1/skills?includeInactive=true", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="skills-heading" className="space-y-4">
      <h2 id="skills-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!skills ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <SkillsPanel initial={skills} csrfToken={session.data.csrfToken} canManage={user!.permissions.includes("directory.manage")} />
        </>
      )}
    </section>
  );
}

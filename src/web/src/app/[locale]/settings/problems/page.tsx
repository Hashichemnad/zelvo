import { getTranslations, setRequestLocale } from "next-intl/server";
import { ProblemSettingsForm } from "@/components/problems/ProblemSettingsForm";
import { getJsonAs } from "@/lib/api";
import type { ProblemSettings } from "@/lib/problems";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/problems">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.problems" });
  return { title: t("title") };
}

/** Settings → Problems (task H1, problems.manage): the known error prefix and the fishbone template. */
export default async function ProblemSettingsPage({ params }: PageProps<"/[locale]/settings/problems">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/problems`);
  const [t, tSection] = await Promise.all([
    getTranslations({ locale, namespace: "problems.settings" }),
    getTranslations({ locale, namespace: "settings.sections.problems" }),
  ]);
  const canManage = (user?.permissions ?? []).includes("problems.manage");
  const settings = canManage ? await getJsonAs<ProblemSettings>("/api/v1/settings/problems", session.data.accessToken, tenant) : null;

  return (
    <section aria-labelledby="problem-settings-heading" className="space-y-6">
      <h2 id="problem-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!settings ? <p role="alert">{t("noAccess")}</p> : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <ProblemSettingsForm initial={settings} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

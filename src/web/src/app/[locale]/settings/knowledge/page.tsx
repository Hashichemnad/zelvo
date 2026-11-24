import { getTranslations, setRequestLocale } from "next-intl/server";
import { KnowledgeSettingsForm } from "@/components/settings/KnowledgeSettingsForm";
import { getJsonAs } from "@/lib/api";
import type { KnowledgeSettings } from "@/lib/knowledge";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/knowledge">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.knowledge" });
  return { title: t("title") };
}

/** Settings → Knowledge (module 7): public reading and the number prefix. */
export default async function KnowledgeSettingsPage({ params }: PageProps<"/[locale]/settings/knowledge">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/knowledge`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.knowledge" }),
    getTranslations({ locale, namespace: "settings.sections.knowledge" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("settings.manage") ?? false;
  const settings = allowed ? await getJsonAs<KnowledgeSettings>("/api/v1/settings/knowledge", session.data.accessToken, tenant) : null;
  return (
    <section aria-labelledby="knowledge-settings-heading" className="space-y-4">
      <h2 id="knowledge-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!settings ? (
        <p role="alert">{tSettings(allowed ? "unavailable" : "noAccess")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <KnowledgeSettingsForm initial={settings} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

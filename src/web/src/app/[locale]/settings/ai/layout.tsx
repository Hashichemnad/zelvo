import { getTranslations, setRequestLocale } from "next-intl/server";
import { AiTabs } from "@/components/settings/ai/AiTabs";
import { requireSignedIn } from "@/lib/signed-in";

/** Settings → AI (module 9): heading, introduction and the page tabs. */
export default async function AiLayout({ children, params }: LayoutProps<"/[locale]/settings/ai">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { user } = await requireSignedIn(locale, `/${locale}/settings/ai`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.ai" }),
    getTranslations({ locale, namespace: "settings.sections.ai" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("ai.manage") ?? false;

  return (
    <section aria-labelledby="ai-heading" className="space-y-4">
      <h2 id="ai-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!allowed ? (
        <p role="alert">{tSettings("noAccess")}</p>
      ) : (
        <>
          <p className="max-w-3xl">{t("intro")}</p>
          <AiTabs />
          {children}
        </>
      )}
    </section>
  );
}

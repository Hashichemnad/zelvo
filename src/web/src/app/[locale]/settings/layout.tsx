import { getTranslations, setRequestLocale } from "next-intl/server";
import { SettingsNav } from "@/components/settings/SettingsNav";
import { sectionsFor } from "@/lib/settings-sections";
import { requireSignedIn } from "@/lib/signed-in";
import { MissingTranslationsWarning } from "./MissingTranslationsWarning";

/**
 * The Settings shell: a menu of the sections the user's roles allow, the missing-translation
 * warning for those who may see it (LANG-5), and the section itself.
 */
export default async function SettingsLayout({ children, params }: LayoutProps<"/[locale]/settings">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { user } = await requireSignedIn(locale, `/${locale}/settings`);
  const t = await getTranslations({ locale, namespace: "settings" });

  if (!user) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  const sections = sectionsFor(user.permissions);

  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
      {sections.length === 0 ? (
        <p role="alert" data-testid="settings-no-access">
          {t("noAccess")}
        </p>
      ) : (
        <>
          {user.permissions.includes("translations.read") && <MissingTranslationsWarning />}
          <div className="grid gap-6 md:grid-cols-[14rem_1fr]">
            <SettingsNav sections={sections} />
            <div className="min-w-0">{children}</div>
          </div>
        </>
      )}
    </div>
  );
}

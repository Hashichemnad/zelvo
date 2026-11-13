import { getTranslations, setRequestLocale } from "next-intl/server";
import { LocalizationSettingsForm } from "@/components/settings/LocalizationSettingsForm";
import { getJsonAs, type LocalizationSettings } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/language">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.language" });
  return { title: t("title") };
}

/** Settings → Language and region. */
export default async function LanguageSettingsPage({ params }: PageProps<"/[locale]/settings/language">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/language`);
  const [t, tSection, display] = await Promise.all([
    getTranslations({ locale, namespace: "settings.language" }),
    getTranslations({ locale, namespace: "settings.sections.language" }),
    getDisplayFormat(),
  ]);
  const settings = user?.permissions.includes("settings.manage")
    ? await getJsonAs<LocalizationSettings>("/api/v1/settings/localization", session.data.accessToken, tenant)
    : null;
  const tSettings = await getTranslations({ locale, namespace: "settings" });

  return (
    <section aria-labelledby="language-settings-heading" className="space-y-4">
      <h2 id="language-settings-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!settings ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <p className="text-sm text-ink-muted">
            {settings.updatedAt
              ? t("lastChanged", { date: formatDateTime(new Date(settings.updatedAt), { locale, ...display }) })
              : t("neverChanged")}
          </p>
          <LocalizationSettingsForm
            csrfToken={session.data.csrfToken}
            initial={{
              defaultLanguage: settings.defaultLanguage,
              defaultCalendar: settings.defaultCalendar,
              defaultDigits: settings.defaultDigits,
            }}
          />
        </>
      )}
    </section>
  );
}

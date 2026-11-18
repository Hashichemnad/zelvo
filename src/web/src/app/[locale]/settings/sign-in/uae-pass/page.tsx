import { getTranslations, setRequestLocale } from "next-intl/server";
import { UaePassSettingsForm, type UaePassSettings } from "@/components/settings/UaePassSettingsForm";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/sign-in/uae-pass">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.uaePass" });
  return { title: t("title") };
}

/** Settings → Sign-in → UAE PASS (GOV-1). Needs identity.manage. */
export default async function UaePassSettingsPage({ params }: PageProps<"/[locale]/settings/sign-in/uae-pass">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/sign-in/uae-pass`);
  const [t, tSettings, display] = await Promise.all([
    getTranslations({ locale, namespace: "settings.uaePass" }),
    getTranslations({ locale, namespace: "settings" }),
    getDisplayFormat(),
  ]);
  const settings = user?.permissions.includes("identity.manage")
    ? await getJsonAs<UaePassSettings>("/api/v1/identity/uae-pass", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="uae-pass-settings-heading" className="space-y-4">
      <h2 id="uae-pass-settings-heading" className="text-2xl font-bold">
        {t("title")}
      </h2>
      {!settings ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <UaePassSettingsForm csrfToken={session.data.csrfToken} initial={settings} display={display} />
        </>
      )}
    </section>
  );
}

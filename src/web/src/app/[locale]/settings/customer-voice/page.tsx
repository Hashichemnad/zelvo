import { getTranslations, setRequestLocale } from "next-intl/server";
import { VoiceSettingsForm } from "@/components/voice/VoiceSettingsForm";
import { requireSignedIn } from "@/lib/signed-in";
import { loadVoiceSettings } from "@/lib/voice-pages";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/customer-voice">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.customerVoice" });
  return { title: t("title") };
}

/** Settings → Customer voice (module 3, task C6; GOV-2): anonymous submission, severities, root causes and the happiness meter. */
export default async function CustomerVoiceSettingsPage({ params }: PageProps<"/[locale]/settings/customer-voice">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/customer-voice`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "voice.settings" }),
    getTranslations({ locale, namespace: "settings.sections.customerVoice" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("voice.manage") ?? false;
  const settings = allowed ? await loadVoiceSettings(session.data.accessToken, tenant) : null;
  return (
    <section aria-labelledby="voice-settings-heading" className="space-y-4">
      <h2 id="voice-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!settings ? (
        <p role="alert">{tSettings(allowed ? "unavailable" : "noAccess")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <VoiceSettingsForm settings={settings} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { TrackVoice } from "@/components/voice/TrackVoice";
import { getDisplayFormat } from "@/lib/display";
import { loadVoiceSettings, optionalSession } from "@/lib/voice-pages";

export async function generateMetadata({ params }: PageProps<"/[locale]/track">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "voice.track" });
  return { title: t("title") };
}

/** Following an anonymous complaint, inquiry or suggestion with its number and tracking code (task C6). */
export default async function TrackPage({ params }: PageProps<"/[locale]/track">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "voice.track" });
  const { session, tenant } = await optionalSession();
  const [settings, format] = await Promise.all([loadVoiceSettings(session?.data.accessToken ?? "", tenant), getDisplayFormat()]);
  return (
    <section aria-labelledby="track-heading" className="space-y-4">
      <h1 id="track-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-2xl">{t("intro")}</p>
      <TrackVoice settings={settings} display={format} />
    </section>
  );
}

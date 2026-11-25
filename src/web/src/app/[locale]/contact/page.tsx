import { getTranslations, setRequestLocale } from "next-intl/server";
import { PortalHappiness } from "@/components/voice/VoiceViews";
import { Link } from "@/i18n/navigation";
import { loadVoiceSettings, optionalSession } from "@/lib/voice-pages";
import { typeSlug, voiceTypes } from "@/lib/voice";

export async function generateMetadata({ params }: PageProps<"/[locale]/contact">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "voice.contact" });
  return { title: t("title") };
}

/** "Contact us" (module 3, task C6): send a complaint, inquiry or suggestion, follow an anonymous one, and rate our services (GOV-2). */
export default async function ContactPage({ params }: PageProps<"/[locale]/contact">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "voice.contact" });
  const { session, tenant } = await optionalSession();
  const settings = await loadVoiceSettings(session?.data.accessToken ?? "", tenant);
  return (
    <section aria-labelledby="contact-heading" className="space-y-8">
      <div className="space-y-2">
        <h1 id="contact-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p className="max-w-2xl">{t("intro")}</p>
      </div>
      <ul className="grid gap-4 sm:grid-cols-3">
        {voiceTypes.map((type) => (
          <li key={type}>
            <Link href={`/contact/${typeSlug[type]}`} className="block h-full rounded-lg border border-line p-4 hover:border-brand focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand" data-testid={`contact-${typeSlug[type]}`}>
              <span className="block text-lg font-semibold text-brand">{t(`types.${type}.name`)}</span>
              <span className="block text-sm text-ink-muted">{t(`types.${type}.hint`)}</span>
            </Link>
          </li>
        ))}
      </ul>
      <p>
        {session ? (
          <Link href="/my-voice" className="font-semibold text-brand underline">{t("mine")}</Link>
        ) : settings?.anonymousEnabled ? (
          <Link href="/track" className="font-semibold text-brand underline">{t("track")}</Link>
        ) : null}
      </p>
      {settings?.happiness.enabled && settings.happiness.inPortal && (
        <PortalHappiness settings={settings.happiness} csrfToken={session?.data.csrfToken ?? null} />
      )}
    </section>
  );
}

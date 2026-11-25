import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { VoiceForm } from "@/components/voice/VoiceForm";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { loadVoiceSettings, optionalSession } from "@/lib/voice-pages";
import { typeOfSlug } from "@/lib/voice";

export async function generateMetadata({ params }: PageProps<"/[locale]/contact/[type]">) {
  const { locale, type } = await params;
  const t = await getTranslations({ locale, namespace: "voice.contact" });
  const voiceType = typeOfSlug(type);
  return { title: voiceType ? t(`types.${voiceType}.send`) : t("title") };
}

/**
 * Sending a complaint, inquiry or suggestion. Signed in: with the record type's form. Visitors:
 * anonymously when the organisation allows it, otherwise they sign in first.
 */
export default async function SendVoicePage({ params }: PageProps<"/[locale]/contact/[type]">) {
  const { locale, type: slug } = await params;
  setRequestLocale(locale);
  const type = typeOfSlug(slug);
  if (!type) notFound();
  const t = await getTranslations({ locale, namespace: "voice.contact" });
  const { session, tenant } = await optionalSession();
  const signIn = `/auth/login?${new URLSearchParams({ locale, returnTo: `/${locale}/contact/${slug}` })}`;
  const settings = session ? null : await loadVoiceSettings("", tenant);
  const choices = session ? await loadDirectoryChoices(session.data.accessToken, tenant, []) : { categories: [], locations: [] };
  return (
    <section aria-labelledby="send-heading" className="space-y-4">
      <h1 id="send-heading" className="text-3xl font-bold text-brand">{t(`types.${type}.send`)}</h1>
      <p className="max-w-2xl">{t(`types.${type}.intro`)}</p>
      {session ? (
        <VoiceForm type={type} csrfToken={session.data.csrfToken} categories={choices.categories} locations={choices.locations} />
      ) : settings?.anonymousEnabled ? (
        <>
          <p><a href={signIn} className="font-semibold text-brand underline">{t("signInInstead")}</a></p>
          <VoiceForm type={type} csrfToken={null} categories={[]} locations={[]} />
        </>
      ) : (
        <p><a href={signIn} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("signInToSend")}</a></p>
      )}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { PasskeySettingsForm } from "@/components/identity/PasskeySettingsForm";
import { getJsonAs } from "@/lib/api";
import type { PasskeyMode } from "@/lib/passkeys";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/sign-in/passkeys">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.signIn.passkeys" });
  return { title: t("title") };
}

/** Settings → Sign-in → Passkeys (task J3). Needs identity.manage. */
export default async function PasskeySettingsPage({ params }: PageProps<"/[locale]/settings/sign-in/passkeys">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { user, session, tenant } = await requireSignedIn(locale, `/${locale}/settings/sign-in/passkeys`);
  const [t, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.signIn.passkeys" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("identity.manage") ?? false;
  const settings = allowed ? await getJsonAs<{ passkeys: PasskeyMode }>("/api/v1/identity/settings", session.data.accessToken, tenant) : null;

  return (
    <section aria-labelledby="passkey-settings-heading" className="space-y-4">
      <p>
        <Link href="/settings/sign-in" className="text-brand underline">{t("back")}</Link>
      </p>
      <h2 id="passkey-settings-heading" className="text-2xl font-bold">{t("title")}</h2>
      {!allowed || !settings ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <PasskeySettingsForm initial={settings.passkeys} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

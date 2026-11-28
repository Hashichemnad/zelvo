import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/sign-in">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.signIn" });
  return { title: t("title") };
}

/** Settings → Sign-in: how people sign in. Needs identity.manage. */
export default async function SignInSettingsPage({ params }: PageProps<"/[locale]/settings/sign-in">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { user } = await requireSignedIn(locale, `/${locale}/settings/sign-in`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.signIn" }),
    getTranslations({ locale, namespace: "settings.sections.signIn" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("identity.manage") ?? false;

  return (
    <section aria-labelledby="sign-in-settings-heading" className="space-y-4">
      <h2 id="sign-in-settings-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!allowed ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <ul className="grid gap-3 sm:grid-cols-2">
            <li className="rounded-lg border border-line bg-surface p-4">
              <h3 className="font-semibold">
                <Link href="/settings/sign-in/uae-pass" className="text-brand underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
                  {t("uaePass")}
                </Link>
              </h3>
              <p className="text-sm text-ink-muted">{t("uaePassDescription")}</p>
            </li>
            {/* Task J3: passkeys. */}
            <li className="rounded-lg border border-line bg-surface p-4">
              <h3 className="font-semibold">
                <Link href="/settings/sign-in/passkeys" className="text-brand underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
                  {t("passkeys.title")}
                </Link>
              </h3>
              <p className="text-sm text-ink-muted">{t("passkeysDescription")}</p>
            </li>
          </ul>
        </>
      )}
    </section>
  );
}

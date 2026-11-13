import { redirect } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { getDirection, otherLocale, type Locale } from "@/i18n/routing";
import { PreferencesForm } from "@/components/PreferencesForm";
import { getCurrentUser, getLocalizationSettings, type LocalizedText } from "@/lib/api";
import { getAuthConfig } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";

export async function generateMetadata({ params }: PageProps<"/[locale]/account">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "account" });
  return { title: t("title") };
}

/** Text in the page's language, or the other language (marked as such) when it is missing. */
function localized(text: LocalizedText, locale: Locale) {
  const other = otherLocale(locale);
  const value = text[locale];
  return value
    ? { value, lang: undefined, dir: undefined }
    : { value: text[other] ?? "", lang: other, dir: getDirection(other) };
}

/** The signed-in user's account: who they are, their roles and how they signed in. */
export default async function AccountPage({ params }: PageProps<"/[locale]/account">) {
  const { locale: rawLocale } = await params;
  const locale = rawLocale as Locale;
  setRequestLocale(locale);

  const session = await getSession();
  if (!session) {
    redirect(`/auth/login?${new URLSearchParams({ locale, returnTo: `/${locale}/account` })}`);
  }
  const tenant = getAuthConfig().tenant;
  const [user, tenantSettings, t, tPreferences] = await Promise.all([
    getCurrentUser(session.data.accessToken, tenant),
    getLocalizationSettings(tenant),
    getTranslations({ locale, namespace: "account" }),
    getTranslations({ locale, namespace: "preferences" }),
  ]);

  if (!user) {
    return (
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p role="alert">{t("unavailable")}</p>
      </section>
    );
  }

  return (
    <section className="space-y-8" aria-labelledby="account-heading">
      <h1 id="account-heading" className="text-3xl font-bold text-brand">
        {t("title")}
      </h1>

      <dl className="grid gap-4 sm:grid-cols-2">
        <div className="rounded-lg border border-line bg-surface p-4">
          <dt className="text-sm text-ink-muted">{t("name")}</dt>
          <dd className="text-lg font-semibold">{user.displayName ?? "—"}</dd>
        </div>
        <div className="rounded-lg border border-line bg-surface p-4">
          <dt className="text-sm text-ink-muted">{t("email")}</dt>
          <dd className="text-lg font-semibold" dir="ltr">
            {user.email ?? "—"}
          </dd>
        </div>
        <div className="rounded-lg border border-line bg-surface p-4">
          <dt className="text-sm text-ink-muted">{t("secondFactor")}</dt>
          <dd className="text-lg font-semibold" data-testid="mfa-status">
            {user.mfa.satisfied ? t("secondFactorUsed") : t("secondFactorNotUsed")}
          </dd>
        </div>
      </dl>

      <section aria-labelledby="preferences-heading" className="space-y-3">
        <h2 id="preferences-heading" className="text-xl font-bold">
          {tPreferences("heading")}
        </h2>
        <p className="max-w-2xl text-ink-muted">{tPreferences("intro")}</p>
        <PreferencesForm
          initial={user.preferences}
          csrfToken={session.data.csrfToken}
          defaults={
            tenantSettings
              ? { language: tenantSettings.defaultLanguage, calendar: tenantSettings.defaultCalendar, digits: tenantSettings.defaultDigits }
              : { language: "en", calendar: "Gregorian", digits: "Western" }
          }
        />
      </section>

      <section aria-labelledby="roles-heading" className="space-y-3">
        <h2 id="roles-heading" className="text-xl font-bold">
          {t("roles")}
        </h2>
        <ul className="flex flex-wrap gap-2" data-testid="roles">
          {user.roles.map((role) => {
            const name = localized(role.name, locale);
            return (
              <li key={role.key} className="rounded-full border border-line bg-surface-muted px-3 py-1 text-sm">
                <span lang={name.lang} dir={name.dir}>
                  {name.value}
                </span>
              </li>
            );
          })}
        </ul>
      </section>
    </section>
  );
}

import { redirect } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getDirection, otherLocale, type Locale } from "@/i18n/routing";
import { PreferencesForm } from "@/components/PreferencesForm";
import { AvailabilitySwitch } from "@/components/directory/AvailabilitySwitch";
import { NotificationPreferencesForm } from "@/components/notifications/NotificationPreferencesForm";
import { NotificationChannelsForm } from "@/components/notifications/NotificationChannelsForm";
import { PushDevicesPanel } from "@/components/notifications/PushDevicesPanel";
import type { PushStatus } from "@/lib/pwa/push";
import type { MyNotificationChannels } from "@/lib/messaging";
import { getCurrentUser, getJsonAs, getLocalizationSettings, type LocalizedText } from "@/lib/api";
import type { NotificationPreference } from "@/lib/notifications";
import { getRequestAuthConfig } from "@/lib/auth/config";
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
  const tenant = (await getRequestAuthConfig()).tenant;
  const [user, tenantSettings, t, tPreferences, tNotifications, notificationPreferences, notificationChannels, push] = await Promise.all([
    getCurrentUser(session.data.accessToken, tenant),
    getLocalizationSettings(tenant),
    getTranslations({ locale, namespace: "account" }),
    getTranslations({ locale, namespace: "preferences" }),
    getTranslations({ locale, namespace: "notifications.preferences" }),
    getJsonAs<NotificationPreference[]>("/api/v1/me/notification-preferences", session.data.accessToken, tenant),
    // Task E3: WhatsApp, SMS and Teams.
    getJsonAs<MyNotificationChannels>("/api/v1/me/notification-channels", session.data.accessToken, tenant),
    // Task J2: web push devices.
    getJsonAs<PushStatus>("/api/v1/me/push", session.data.accessToken, tenant),
  ]);

  if (!user) {
    return (
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p role="alert">{t("unavailable")}</p>
      </section>
    );
  }

  // Task K4: staff (anyone with a permission) see their own workload; requesters have none.
  const isStaff = user.permissions.length > 0;
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

      {/* Task J3: passkeys. */}
      <section aria-labelledby="security-link-heading" className="space-y-2">
        <h2 id="security-link-heading" className="text-xl font-bold">{t("security.heading")}</h2>
        <p className="max-w-2xl text-ink-muted">{t("security.intro")}</p>
        <Link href="/account/security" className="font-semibold text-brand underline" data-testid="account-security-link">{t("security.link")}</Link>
      </section>

      {/* Task K4: the agent's own wellbeing figures. */}
      {isStaff && (
        <section aria-labelledby="workload-heading" className="space-y-2">
          <h2 id="workload-heading" className="text-xl font-bold">{t("workload.heading")}</h2>
          <p className="max-w-2xl text-ink-muted">{t("workload.intro")}</p>
          <Link href="/account/workload" className="font-semibold text-brand underline" data-testid="account-workload-link">{t("workload.link")}</Link>
        </section>
      )}
      {/* Task F5: the privacy notice, consent, a copy of one's data and privacy requests. */}
      <section aria-labelledby="privacy-heading" className="space-y-2">
        <h2 id="privacy-heading" className="text-xl font-bold">{t("privacy.heading")}</h2>
        <p className="max-w-2xl text-ink-muted">{t("privacy.intro")}</p>
        <Link href="/account/privacy" className="font-semibold text-brand underline" data-testid="account-privacy-link">{t("privacy.link")}</Link>
      </section>

      {notificationPreferences && (
        <section id="notifications" aria-labelledby="notification-preferences-heading" className="space-y-3">
          <h2 id="notification-preferences-heading" className="text-xl font-bold">
            {tNotifications("heading")}
          </h2>
          <p className="max-w-2xl text-ink-muted">{tNotifications("intro")}</p>
          <NotificationPreferencesForm initial={notificationPreferences} csrfToken={session.data.csrfToken} />
          {push && <PushDevicesPanel initial={push} csrfToken={session.data.csrfToken} />}
          {notificationChannels && <NotificationChannelsForm initial={notificationChannels} csrfToken={session.data.csrfToken} />}
        </section>
      )}

      {user.permissions.includes("incidents.edit") && (
        <section aria-labelledby="availability-heading" className="space-y-3" data-testid="availability">
          <h2 id="availability-heading" className="text-xl font-bold">
            {t("availability.heading")}
          </h2>
          <AvailabilitySwitch initial={user.availableForAssignment ?? true} csrfToken={session.data.csrfToken} />
          {user.groups && user.groups.length > 0 && (
            <>
              <h3 className="font-semibold">{t("availability.groups")}</h3>
              <ul className="flex flex-wrap gap-2">
                {user.groups.map((group) => {
                  const name = localized(group.name, locale);
                  return (
                    <li key={group.id} className="rounded-full border border-line bg-surface-muted px-3 py-1 text-sm">
                      <span lang={name.lang} dir={name.dir}>
                        {name.value}
                      </span>
                      {group.isLead && ` · ${t("availability.lead")}`}
                    </li>
                  );
                })}
              </ul>
            </>
          )}
        </section>
      )}

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

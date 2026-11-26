import { getTranslations, setRequestLocale } from "next-intl/server";
import { LicensePanel } from "@/components/license/LicensePanel";
import { ReleaseComponents } from "@/components/license/ReleaseComponents";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { LicenseState, ReleaseComponentsState } from "@/lib/license";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/license">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.license" });
  return { title: t("title") };
}

/**
 * Settings → License (module 12, task F3): activation, the license and these servers' fingerprint;
 * with task F6 also the release integrity of each component.
 */
export default async function LicenseSettingsPage({ params }: PageProps<"/[locale]/settings/license">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/license`);
  const [tSection, tSettings, t] = await Promise.all([
    getTranslations({ locale, namespace: "settings.sections.license" }),
    getTranslations({ locale, namespace: "settings" }),
    getTranslations({ locale, namespace: "license" }),
  ]);
  const allowed = user?.permissions.includes("installation.manage") ?? false;
  const [license, format, release] = allowed
    ? await Promise.all([
        getJsonAs<LicenseState>("/api/v1/license", session.data.accessToken, tenant),
        getDisplayFormat(),
        getJsonAs<ReleaseComponentsState>("/api/v1/license/components", session.data.accessToken, tenant),
      ])
    : [null, null, null];
  return (
    <section aria-labelledby="license-heading" className="space-y-4">
      <h2 id="license-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!license || !format ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <LicensePanel initial={license} csrfToken={session.data.csrfToken} calendar={format.calendar} digits={format.digits} timeZone={format.timeZone} />
          {release && (
            <ReleaseComponents state={release} locale={locale} calendar={format.calendar} digits={format.digits} timeZone={format.timeZone} />
          )}
        </>
      )}
    </section>
  );
}

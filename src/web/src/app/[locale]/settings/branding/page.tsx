import { getTranslations, setRequestLocale } from "next-intl/server";
import { BrandingPanel } from "@/components/settings/BrandingPanel";
import { getJsonAs } from "@/lib/api";
import type { BrandingSettings, CustomDomain, SenderDomain } from "@/lib/branding";
import { requireSignedIn } from "@/lib/signed-in";
import { getRequestAuthConfig } from "@/lib/auth/config";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/branding">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.branding" });
  return { title: t("title") };
}

/** Settings → Branding (module 11, task F1). */
export default async function BrandingSettingsPage({ params }: PageProps<"/[locale]/settings/branding">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/branding`);
  if (!user?.permissions.includes("branding.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [settings, domains, sender, app, config, t] = await Promise.all([
    getJsonAs<BrandingSettings>("/api/v1/settings/branding", token, tenant),
    getJsonAs<CustomDomain[]>("/api/v1/settings/branding/domains", token, tenant),
    getJsonAs<SenderDomain>("/api/v1/settings/branding/email", token, tenant),
    // Task J1: the installable app's settings and the addresses its QR code can open.
    getJsonAs<{ offlineData: boolean }>("/api/v1/settings/app", token, tenant),
    getRequestAuthConfig(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!settings || !domains) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <BrandingPanel initial={settings} domains={domains} sender={sender} tenant={tenant} csrfToken={session.data.csrfToken}
      app={{ offlineData: app?.offlineData ?? true, address: config.appUrl.origin }} />
  );
}

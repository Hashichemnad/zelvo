import { getTranslations, setRequestLocale } from "next-intl/server";
import { AssetSettingsForm } from "@/components/assets/AssetSettingsForm";
import { getJsonAs } from "@/lib/api";
import type { AssetSettings } from "@/lib/assets";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/assets">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.assets" });
  return { title: t("title") };
}

/** Settings → Assets (task G3, assets.manage): generated tags and expiry alerts. */
export default async function AssetSettingsPage({ params }: PageProps<"/[locale]/settings/assets">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/assets`);
  const [tSection, tSettings, t] = await Promise.all([
    getTranslations({ locale, namespace: "settings.sections.assets" }),
    getTranslations({ locale, namespace: "settings" }),
    getTranslations({ locale, namespace: "assets.settings" }),
  ]);
  const permissions = user?.permissions ?? [];
  const settings = permissions.includes("assets.manage") ? await getJsonAs<AssetSettings>("/api/v1/assets/settings", session.data.accessToken, tenant) : null;
  return (
    <section aria-labelledby="asset-settings-heading" className="space-y-4">
      <h2 id="asset-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!settings ? <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p> : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <AssetSettingsForm initial={settings} csrfToken={session.data.csrfToken} canManage />
        </>
      )}
    </section>
  );
}

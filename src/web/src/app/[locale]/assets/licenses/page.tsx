import { getTranslations, setRequestLocale } from "next-intl/server";
import { LicensesPanel } from "@/components/assets/LicensesPanel";
import { AssetsNav } from "@/components/assets/AssetsNav";
import { getJsonAs } from "@/lib/api";
import type { SoftwareLicense, AssetSettings } from "@/lib/assets";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/assets/licenses">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "assets.licenses" });
  return { title: t("title") };
}

/** Software licences and compliance (task G3, assets.read; changes assets.manage). */
export default async function LicensesPage({ params }: PageProps<"/[locale]/assets/licenses">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/assets/licenses`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "assets.licenses" }), getTranslations({ locale, namespace: "assets.list" })]);
  const permissions = user?.permissions ?? [];
  const allowed = permissions.includes("assets.read") || permissions.includes("assets.manage");
  const token = session.data.accessToken;
  const [licenses, settings, format] = await Promise.all([
    allowed ? getJsonAs<SoftwareLicense[]>("/api/v1/assets/licenses", token, tenant) : Promise.resolve(null),
    allowed ? getJsonAs<AssetSettings>("/api/v1/assets/settings", token, tenant) : Promise.resolve(null),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="licenses-heading" className="space-y-6">
      <h1 id="licenses-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <AssetsNav current="licenses" />
      <p className="max-w-3xl">{t("intro")}</p>
      {!licenses ? <p role="alert">{user && !allowed ? tList("noAccess") : tList("unavailable")}</p> : (
        <LicensesPanel initial={licenses} canManage={permissions.includes("assets.manage")} csrfToken={session.data.csrfToken} display={{ locale, ...format }}
          warningDays={settings?.expiryWarningDays ?? 30} />
      )}
    </section>
  );
}

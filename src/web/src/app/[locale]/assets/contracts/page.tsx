import { getTranslations, setRequestLocale } from "next-intl/server";
import { ContractsPanel } from "@/components/assets/ContractsPanel";
import { AssetsNav } from "@/components/assets/AssetsNav";
import { getJsonAs } from "@/lib/api";
import type { AssetContract, AssetSettings } from "@/lib/assets";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/assets/contracts">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "assets.contracts" });
  return { title: t("title") };
}

/** Contracts and warranties (task G3, assets.read; changes assets.manage). */
export default async function ContractsPage({ params }: PageProps<"/[locale]/assets/contracts">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/assets/contracts`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "assets.contracts" }), getTranslations({ locale, namespace: "assets.list" })]);
  const permissions = user?.permissions ?? [];
  const allowed = permissions.includes("assets.read") || permissions.includes("assets.manage");
  const token = session.data.accessToken;
  const [contracts, settings, format] = await Promise.all([
    allowed ? getJsonAs<AssetContract[]>("/api/v1/assets/contracts", token, tenant) : Promise.resolve(null),
    allowed ? getJsonAs<AssetSettings>("/api/v1/assets/settings", token, tenant) : Promise.resolve(null),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="contracts-heading" className="space-y-6">
      <h1 id="contracts-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <AssetsNav current="contracts" />
      <p className="max-w-3xl">{t("intro")}</p>
      {!contracts ? <p role="alert">{user && !allowed ? tList("noAccess") : tList("unavailable")}</p> : (
        <ContractsPanel initial={contracts} canManage={permissions.includes("assets.manage")} csrfToken={session.data.csrfToken} display={{ locale, ...format }}
          warningDays={settings?.expiryWarningDays ?? 30} />
      )}
    </section>
  );
}

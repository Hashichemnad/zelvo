import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { AssetDetail } from "@/components/assets/AssetDetail";
import { AssetsNav } from "@/components/assets/AssetsNav";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { Asset, AssetSettings } from "@/lib/assets";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/assets/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "assets.detail" });
  return { title: t("title") };
}

/** One asset (task G3, assets.read): lifecycle, person, purchase, contracts and licences. */
export default async function AssetPage({ params }: PageProps<"/[locale]/assets/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/assets/${id}`);
  const t = await getTranslations({ locale, namespace: "assets.list" });
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("assets.read") && !permissions.includes("assets.manage")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [asset, settings, format] = await Promise.all([
    getJsonAs<Asset>(`/api/v1/assets/${id}`, token, tenant),
    getJsonAs<AssetSettings>("/api/v1/assets/settings", token, tenant),
    getDisplayFormat(),
  ]);
  if (!asset) notFound();
  return (
    <article aria-labelledby="asset-heading" className="space-y-6">
      <Link href="/assets" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <AssetsNav current="assets" />
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{asset.tag}</p>
        <h1 id="asset-heading" className="text-3xl font-bold text-brand" dir="auto">{asset.name}</h1>
      </header>
      <AssetDetail initial={asset} canManage={permissions.includes("assets.manage")} csrfToken={session.data.csrfToken} display={{ locale, ...format }}
        warningDays={settings?.expiryWarningDays ?? 30} />
    </article>
  );
}

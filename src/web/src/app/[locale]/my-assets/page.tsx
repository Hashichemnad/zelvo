import { getTranslations, setRequestLocale } from "next-intl/server";
import { PersonAssets } from "@/components/assets/PersonAssets";
import { getJsonAs } from "@/lib/api";
import type { PersonAsset } from "@/lib/assets";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-assets">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "assets.mine" });
  return { title: t("title") };
}

/** My assets (task G3): the laptops, phones and other assets assigned to the person, with their warranty. */
export default async function MyAssetsPage({ params }: PageProps<"/[locale]/my-assets">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-assets`);
  const t = await getTranslations({ locale, namespace: "assets.mine" });
  const [assets, format] = await Promise.all([getJsonAs<PersonAsset[]>("/api/v1/me/assets", session.data.accessToken, tenant), getDisplayFormat()]);
  return (
    <section aria-labelledby="my-assets-heading" className="space-y-6">
      <h1 id="my-assets-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-2xl">{t("intro")}</p>
      {!assets ? <p role="alert">{t("unavailable")}</p> : <PersonAssets assets={assets} display={{ locale, ...format }} staff={false} />}
    </section>
  );
}

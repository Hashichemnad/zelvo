import { getTranslations, setRequestLocale } from "next-intl/server";
import { AiPricesPanel } from "@/components/settings/ai/AiPricesPanel";
import type { AiPrice, AiProvider } from "@/lib/ai";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/ai/prices">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.ai.tabs" });
  return { title: t("prices") };
}

/** Settings → AI → Prices of models. */
export default async function AiPricesPage({ params }: PageProps<"/[locale]/settings/ai/prices">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/ai/prices`);
  if (!user?.permissions.includes("ai.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [prices, providers, display, t] = await Promise.all([
    getJsonAs<AiPrice[]>("/api/v1/ai/prices", token, tenant),
    getJsonAs<AiProvider[]>("/api/v1/ai/providers", token, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!prices || !providers) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <AiPricesPanel initial={prices} providers={providers} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />;
}

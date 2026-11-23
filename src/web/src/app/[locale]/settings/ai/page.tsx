import { getTranslations, setRequestLocale } from "next-intl/server";
import { AiProvidersPanel } from "@/components/settings/ai/AiProvidersPanel";
import type { AiProvider } from "@/lib/ai";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/ai">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.ai.tabs" });
  return { title: t("providers") };
}

/** Settings → AI → Providers. */
export default async function AiProvidersPage({ params }: PageProps<"/[locale]/settings/ai">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/ai`);
  if (!user?.permissions.includes("ai.manage")) {
    return null;
  }
  const [providers, display, t] = await Promise.all([
    getJsonAs<AiProvider[]>("/api/v1/ai/providers", session.data.accessToken, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!providers) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <AiProvidersPanel initial={providers} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />;
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { AiUsagePanel } from "@/components/settings/ai/AiUsagePanel";
import type { AiUsage } from "@/lib/ai";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/ai/usage">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.ai.tabs" });
  return { title: t("usage") };
}

/** Settings → AI → Usage and cost, with the monthly limit. */
export default async function AiUsagePage({ params }: PageProps<"/[locale]/settings/ai/usage">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/ai/usage`);
  if (!user?.permissions.includes("ai.manage")) {
    return null;
  }
  const [usage, display, t] = await Promise.all([
    getJsonAs<AiUsage>("/api/v1/ai/usage", session.data.accessToken, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!usage) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <AiUsagePanel initial={usage} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />;
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { AiLogPanel } from "@/components/settings/ai/AiLogPanel";
import type { AiCall } from "@/lib/ai";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { Page } from "@/lib/integrations";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/ai/log">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.ai.tabs" });
  return { title: t("log") };
}

/** Settings → AI → AI log. */
export default async function AiLogPage({ params }: PageProps<"/[locale]/settings/ai/log">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/ai/log`);
  if (!user?.permissions.includes("ai.manage")) {
    return null;
  }
  const [calls, display, t] = await Promise.all([
    getJsonAs<Page<AiCall>>("/api/v1/ai/calls?pageSize=25", session.data.accessToken, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!calls) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <AiLogPanel initial={calls} display={{ locale, ...display }} />;
}

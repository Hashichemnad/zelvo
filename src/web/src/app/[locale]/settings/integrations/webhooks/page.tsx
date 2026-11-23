import { getTranslations, setRequestLocale } from "next-intl/server";
import { WebhooksPanel } from "@/components/settings/integrations/WebhooksPanel";
import { getJsonAs } from "@/lib/api";
import type { IntegrationInfo, Page, Webhook } from "@/lib/integrations";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/integrations/webhooks">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.integrations.tabs" });
  return { title: t("webhooks") };
}

/** Settings → Integrations → Webhooks. */
export default async function WebhooksPage({ params }: PageProps<"/[locale]/settings/integrations/webhooks">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/integrations/webhooks`);
  if (!user?.permissions.includes("integrations.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [info, webhooks, t] = await Promise.all([
    getJsonAs<IntegrationInfo>("/api/v1/integrations/info", token, tenant),
    getJsonAs<Page<Webhook>>("/api/v1/integrations/webhooks?pageSize=100", token, tenant),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!info || !webhooks) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <WebhooksPanel initial={webhooks} info={info} csrfToken={session.data.csrfToken} />;
}

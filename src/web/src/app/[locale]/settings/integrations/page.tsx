import { getTranslations, setRequestLocale } from "next-intl/server";
import { ApiClientsPanel } from "@/components/settings/integrations/ApiClientsPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { ApiClient, IntegrationInfo, Page } from "@/lib/integrations";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/integrations">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.integrations.tabs" });
  return { title: t("clients") };
}

/** Settings → Integrations → API keys and OAuth clients. */
export default async function ApiClientsPage({ params }: PageProps<"/[locale]/settings/integrations">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/integrations`);
  if (!user?.permissions.includes("integrations.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [info, clients, display, t] = await Promise.all([
    getJsonAs<IntegrationInfo>("/api/v1/integrations/info", token, tenant),
    getJsonAs<Page<ApiClient>>("/api/v1/integrations/clients?pageSize=100", token, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!info || !clients) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <ApiClientsPanel initial={clients} info={info} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />;
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { IntegrationLogPanel } from "@/components/settings/integrations/IntegrationLogPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { IntegrationInfo, IntegrationLogEntry, Page } from "@/lib/integrations";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/integrations/log">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.integrations.tabs" });
  return { title: t("log") };
}

/** Settings → Integrations → Integration log. */
export default async function IntegrationLogPage({ params }: PageProps<"/[locale]/settings/integrations/log">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/integrations/log`);
  if (!user?.permissions.includes("integrations.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [info, log, display, t, tLog] = await Promise.all([
    getJsonAs<IntegrationInfo>("/api/v1/integrations/info", token, tenant),
    getJsonAs<Page<IntegrationLogEntry>>("/api/v1/integrations/log?pageSize=25", token, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
    getTranslations({ locale, namespace: "settings.integrations.log" }),
  ]);
  if (!info || !log) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <div className="space-y-4">
      <p className="max-w-3xl">{tLog("intro", { days: info.logRetentionDays })}</p>
      <IntegrationLogPanel initial={log} display={{ locale, ...display }} />
    </div>
  );
}

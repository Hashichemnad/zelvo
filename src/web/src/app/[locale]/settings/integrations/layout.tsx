import { getTranslations, setRequestLocale } from "next-intl/server";
import { IntegrationsTabs } from "@/components/settings/integrations/IntegrationsTabs";
import { getJsonAs } from "@/lib/api";
import type { IntegrationInfo } from "@/lib/integrations";
import { requireSignedIn } from "@/lib/signed-in";

/** Settings → Integrations (module 14): heading, the installation's environment and the page tabs. */
export default async function IntegrationsLayout({ children, params }: LayoutProps<"/[locale]/settings/integrations">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/integrations`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.integrations" }),
    getTranslations({ locale, namespace: "settings.sections.integrations" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("integrations.manage") ?? false;
  const info = allowed ? await getJsonAs<IntegrationInfo>("/api/v1/integrations/info", session.data.accessToken, tenant) : null;

  return (
    <section aria-labelledby="integrations-heading" className="space-y-4">
      <h2 id="integrations-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!info ? (
        <p role="alert">{tSettings(allowed ? "unavailable" : "noAccess")}</p>
      ) : (
        <>
          <p className="max-w-3xl">{t("intro")}</p>
          <p className="max-w-3xl text-sm" data-testid="integrations-environment">
            <span className="font-semibold">{t("environment", { environment: t(`environments.${info.environment}`) })}</span>{" "}
            {t("environmentHelp")}
          </p>
          <IntegrationsTabs />
          {children}
        </>
      )}
    </section>
  );
}

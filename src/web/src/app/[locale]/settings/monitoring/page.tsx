import { getTranslations, setRequestLocale } from "next-intl/server";
import { MonitoringPanel } from "@/components/connectors/MonitoringPanel";
import { getJsonAs } from "@/lib/api";
import type { MonitoringSource } from "@/lib/connectors";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/monitoring">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "connectors.monitoring" });
  return { title: t("title") };
}

/** Settings → Monitoring (module 14, task E7): monitoring tools whose alerts become incidents. */
export default async function MonitoringPage({ params }: PageProps<"/[locale]/settings/monitoring">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/monitoring`);
  const t = await getTranslations({ locale, namespace: "connectors.monitoring" });
  if (!user?.permissions.includes("connectors.manage")) {
    return null;
  }
  const [sources, choices, display] = await Promise.all([
    getJsonAs<MonitoringSource[]>("/api/v1/monitoring/sources", session.data.accessToken, tenant),
    loadDirectoryChoices(session.data.accessToken, tenant, user.permissions),
    getDisplayFormat(),
  ]);
  if (!sources) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <section aria-labelledby="monitoring-heading" className="space-y-4">
      <h2 id="monitoring-heading" className="text-2xl font-semibold">{t("title")}</h2>
      <MonitoringPanel initial={sources} csrfToken={session.data.csrfToken} categories={choices.categories} groups={choices.groups ?? []} display={{ locale, ...display }} />
    </section>
  );
}

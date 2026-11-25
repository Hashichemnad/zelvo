import { getTranslations, setRequestLocale } from "next-intl/server";
import { ReportTargetsForm } from "@/components/reports/ReportTargetsForm";
import { getJsonAs } from "@/lib/api";
import type { ReportTargets } from "@/lib/reports";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/reports">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.reports" });
  return { title: t("title") };
}

/** Settings → Reports (module 10, task F2): the SLA and happiness targets of the dashboards. */
export default async function ReportSettingsPage({ params }: PageProps<"/[locale]/settings/reports">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/reports`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "reports.settings" }),
    getTranslations({ locale, namespace: "settings.sections.reports" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("reports.manage") ?? false;
  const targets = allowed ? await getJsonAs<ReportTargets>("/api/v1/settings/reports", session.data.accessToken, tenant) : null;
  return (
    <section aria-labelledby="report-settings-heading" className="space-y-4">
      <h2 id="report-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!targets ? <p role="alert">{tSettings(allowed ? "unavailable" : "noAccess")}</p> : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <ReportTargetsForm targets={targets} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

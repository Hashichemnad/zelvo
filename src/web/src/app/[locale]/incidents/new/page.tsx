import { getTranslations, setRequestLocale } from "next-intl/server";
import { ReportIncidentForm } from "@/components/incidents/ReportIncidentForm";
import { getJsonAs } from "@/lib/api";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { loadAssignees, loadRequesters } from "@/lib/incident-pages";
import type { IncidentSettings } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/incidents/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "incidents" });
  return { title: t("logTitle") };
}

/** Staff log an incident for someone else (phone, email, walk-in), with impact, urgency and assignee. */
export default async function LogIncidentPage({ params }: PageProps<"/[locale]/incidents/new">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/incidents/new`);
  const t = await getTranslations({ locale, namespace: "incidents" });
  if (!user?.permissions.includes("incidents.create")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [settings, requesters, assignees, directory] = await Promise.all([
    getJsonAs<IncidentSettings>("/api/v1/settings/incidents", token, tenant),
    user.permissions.includes("users.read") ? loadRequesters(token, tenant) : Promise.resolve([]),
    user.permissions.includes("incidents.assign") ? loadAssignees(token, tenant) : Promise.resolve(null),
    loadDirectoryChoices(token, tenant, user.permissions),
  ]);

  return (
    <section aria-labelledby="log-heading" className="space-y-4">
      <h1 id="log-heading" className="text-3xl font-bold text-brand">
        {t("logTitle")}
      </h1>
      <p className="max-w-2xl">{t("logIntro")}</p>
      <ReportIncidentForm csrfToken={session.data.csrfToken} settings={settings} basePath="/incidents" staff={{ requesters, assignees }} directory={directory} />
    </section>
  );
}

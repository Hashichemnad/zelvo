import { getTranslations, setRequestLocale } from "next-intl/server";
import { ReportIncidentForm } from "@/components/incidents/ReportIncidentForm";
import { getJsonAs } from "@/lib/api";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { isId } from "@/lib/incident-pages";
import type { AttachmentSettings, Incident, IncidentSettings } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-incidents/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "incidents" });
  return { title: t("reportTitle") };
}

/** "Report an incident" (requesters; reused by the portal, D2). `?followUpOf=<id>` reports a follow-up of a closed incident. */
export default async function ReportIncidentPage({ params, searchParams }: PageProps<"/[locale]/my-incidents/new">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-incidents/new`);
  const t = await getTranslations({ locale, namespace: "incidents" });
  const followUpId = (await searchParams).followUpOf;
  const [settings, followUp, choices, attachmentSettings] = await Promise.all([
    getJsonAs<IncidentSettings>("/api/v1/settings/incidents", session.data.accessToken, tenant),
    typeof followUpId === "string" && isId(followUpId)
      ? getJsonAs<Incident>(`/api/v1/incidents/${followUpId}`, session.data.accessToken, tenant)
      : Promise.resolve(null),
    // Requesters choose only categories and locations; groups are for staff.
    loadDirectoryChoices(session.data.accessToken, tenant, []),
    // Task J1: photos and files with the report (camera on phones), within the tenant's limits.
    getJsonAs<AttachmentSettings>("/api/v1/settings/attachments", session.data.accessToken, tenant),
  ]);

  return (
    <section aria-labelledby="report-heading" className="space-y-4">
      <h1 id="report-heading" className="text-3xl font-bold text-brand">
        {t("reportTitle")}
      </h1>
      <p className="max-w-2xl">{t("reportIntro")}</p>
      <ReportIncidentForm
        csrfToken={session.data.csrfToken}
        settings={settings}
        basePath="/my-incidents"
        followUpOf={followUp?.state === "Closed" ? { id: followUp.id, number: followUp.number } : null}
        directory={{ categories: choices.categories, locations: choices.locations, groups: null }}
        attachments={attachmentSettings ? { maxSizeMb: attachmentSettings.maxSizeMb, allowedExtensions: attachmentSettings.allowedExtensions } : null}
      />
    </section>
  );
}

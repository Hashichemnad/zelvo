import { getTranslations, setRequestLocale } from "next-intl/server";
import { RecordTypesEditor } from "@/components/records/RecordTypesEditor";
import { getJsonAs } from "@/lib/api";
import type { RecordType } from "@/lib/records";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/record-types">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.recordTypes" });
  return { title: t("title") };
}

/** Settings → Record types (task K2, recordtypes.manage): custom record types on the ticket core. */
export default async function RecordTypesSettingsPage({ params }: PageProps<"/[locale]/settings/record-types">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/record-types`);
  const [t, tSection] = await Promise.all([
    getTranslations({ locale, namespace: "records.settings" }),
    getTranslations({ locale, namespace: "settings.sections.recordTypes" }),
  ]);
  const canManage = (user?.permissions ?? []).includes("recordtypes.manage");
  const types = canManage ? await getJsonAs<RecordType[]>("/api/v1/record-types", session.data.accessToken, tenant) : null;

  return (
    <section aria-labelledby="record-types-heading" className="space-y-6">
      <h2 id="record-types-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!types ? <p role="alert">{t("noAccess")}</p> : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <RecordTypesEditor initial={types} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

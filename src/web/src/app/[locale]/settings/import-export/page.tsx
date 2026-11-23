import { getTranslations, setRequestLocale } from "next-intl/server";
import { ImportExportPanel } from "@/components/settings/ImportExportPanel";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/import-export">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.importExport" });
  return { title: t("title") };
}

/** Settings → Import and export (module 14): users in CSV and Excel. */
export default async function ImportExportPage({ params }: PageProps<"/[locale]/settings/import-export">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, user } = await requireSignedIn(locale, `/${locale}/settings/import-export`);
  const [tSection, tSettings, display] = await Promise.all([
    getTranslations({ locale, namespace: "settings.sections.importExport" }),
    getTranslations({ locale, namespace: "settings" }),
    getDisplayFormat(),
  ]);
  const permissions = user?.permissions ?? [];

  return (
    <section aria-labelledby="import-export-heading" className="space-y-4">
      <h2 id="import-export-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!permissions.includes("users.read") ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <ImportExportPanel
          csrfToken={session.data.csrfToken}
          canImport={permissions.includes("users.manage")}
          locale={locale}
          digits={display.digits}
        />
      )}
    </section>
  );
}

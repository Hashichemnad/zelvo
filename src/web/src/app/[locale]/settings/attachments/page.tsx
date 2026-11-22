import { getTranslations, setRequestLocale } from "next-intl/server";
import { AttachmentSettingsForm } from "@/components/settings/AttachmentSettingsForm";
import { getJsonAs } from "@/lib/api";
import type { AttachmentSettings } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/attachments">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.attachments" });
  return { title: t("title") };
}

/** Settings → Attachments: largest file, accepted types, malware scanning status. */
export default async function AttachmentSettingsPage({ params }: PageProps<"/[locale]/settings/attachments">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/attachments`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.attachments" }),
    getTranslations({ locale, namespace: "settings.sections.attachments" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const settings = user?.permissions.includes("settings.manage")
    ? await getJsonAs<AttachmentSettings>("/api/v1/settings/attachments", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="attachment-settings-heading" className="space-y-4">
      <h2 id="attachment-settings-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!settings ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <AttachmentSettingsForm initial={settings} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { NoticeEditor, PurposesEditor, RetentionEditor } from "@/components/privacy/PrivacySettingsPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { ConsentPurpose, PrivacyNotice, PrivacySettings } from "@/lib/privacy";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/privacy">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.privacy" });
  return { title: t("title") };
}

/** Settings → Privacy (GOV-5, task F5): the privacy notice, optional consent purposes and retention periods. */
export default async function PrivacySettingsPage({ params }: PageProps<"/[locale]/settings/privacy">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/privacy`);
  const [tSection, tSettings, t] = await Promise.all([
    getTranslations({ locale, namespace: "settings.sections.privacy" }),
    getTranslations({ locale, namespace: "settings" }),
    getTranslations({ locale, namespace: "privacy.settings" }),
  ]);
  const allowed = user?.permissions.includes("privacy.manage");
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [notices, purposes, settings, format] = allowed
    ? await Promise.all([get<PrivacyNotice[]>("/api/v1/privacy/notices"), get<ConsentPurpose[]>("/api/v1/privacy/purposes"), get<PrivacySettings>("/api/v1/settings/privacy"), getDisplayFormat()])
    : [null, null, null, null];
  return (
    <section aria-labelledby="privacy-settings-heading" className="space-y-8">
      <h2 id="privacy-settings-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!notices || !purposes || !settings || !format ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <NoticeEditor initial={notices} display={{ locale, ...format }} csrfToken={session.data.csrfToken} />
          <PurposesEditor initial={purposes} csrfToken={session.data.csrfToken} />
          <RetentionEditor initial={settings} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

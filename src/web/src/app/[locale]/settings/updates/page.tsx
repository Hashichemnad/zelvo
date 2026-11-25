import { getTranslations, setRequestLocale } from "next-intl/server";
import { UpdatesPanel } from "@/components/updates/UpdatesPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { UpdatesState } from "@/lib/updates";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/updates">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.updates" });
  return { title: t("title") };
}

/** Settings → Updates (module 13, task F4): offline update packages, installs and their history. */
export default async function UpdatesSettingsPage({ params }: PageProps<"/[locale]/settings/updates">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/updates`);
  const [tSection, tSettings, t] = await Promise.all([
    getTranslations({ locale, namespace: "settings.sections.updates" }),
    getTranslations({ locale, namespace: "settings" }),
    getTranslations({ locale, namespace: "updates" }),
  ]);
  const allowed = user?.permissions.includes("installation.manage") ?? false;
  const [updates, format] = allowed
    ? await Promise.all([getJsonAs<UpdatesState>("/api/v1/updates", session.data.accessToken, tenant), getDisplayFormat()])
    : [null, null];
  return (
    <section aria-labelledby="updates-heading" className="space-y-4">
      <h2 id="updates-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!updates || !format ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <UpdatesPanel initial={updates} csrfToken={session.data.csrfToken} calendar={format.calendar} digits={format.digits} timeZone={format.timeZone} />
        </>
      )}
    </section>
  );
}

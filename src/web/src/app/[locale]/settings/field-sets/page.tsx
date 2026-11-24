import { getTranslations, setRequestLocale } from "next-intl/server";
import { FieldSetsPanel } from "@/components/forms/FormsPanel";
import { getJsonAs } from "@/lib/api";
import type { FieldSetDetail } from "@/lib/forms/types";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/field-sets">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.fieldSets" });
  return { title: t("title") };
}

/** Settings → Field sets (task C1): groups of fields shared by many forms; a change reaches every form that uses it. */
export default async function FieldSetsPage({ params }: PageProps<"/[locale]/settings/field-sets">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/field-sets`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "forms.list" }),
    getTranslations({ locale, namespace: "settings.sections.fieldSets" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const sets = user?.permissions.includes("forms.manage") ? await getJsonAs<FieldSetDetail[]>("/api/v1/field-sets", session.data.accessToken, tenant) : null;
  return (
    <section aria-labelledby="field-sets-heading" className="space-y-4">
      <h2 id="field-sets-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!sets ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("introFieldSets")}</p>
          <FieldSetsPanel initial={sets} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}

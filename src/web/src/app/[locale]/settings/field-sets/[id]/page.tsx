import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormBuilder } from "@/components/forms/FormBuilder";
import { FormDetailsPanel } from "@/components/forms/FormsPanel";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { loadBuilderChoices, previewMessages } from "@/lib/forms/pages";
import type { FieldSetDetail } from "@/lib/forms/types";
import { requireSignedIn } from "@/lib/signed-in";

const isId = (value: string) => /^[0-9a-f-]{36}$/i.test(value);

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/field-sets/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "forms.builder" });
  return { title: t("pageTitleFieldSet") };
}

/** One field set in the builder (task C1). */
export default async function FieldSetBuilderPage({ params }: PageProps<"/[locale]/settings/field-sets/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/field-sets/${id}`);
  const [t, tSettings] = await Promise.all([getTranslations({ locale, namespace: "forms.builder" }), getTranslations({ locale, namespace: "settings" })]);
  if (!user?.permissions.includes("forms.manage")) {
    return <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [set, choices, messages] = await Promise.all([getJsonAs<FieldSetDetail>(`/api/v1/field-sets/${id}`, token, tenant), loadBuilderChoices(token, tenant), previewMessages()]);
  if (!set) notFound();
  const name = set.name[locale as "en" | "ar"] ?? set.name.en ?? set.name.ar ?? set.key;

  return (
    <section aria-labelledby="builder-heading" className="space-y-6">
      <Link href="/settings/field-sets" className="text-sm font-semibold text-brand underline">{t("backFieldSets")}</Link>
      <h2 id="builder-heading" className="text-2xl font-bold">{t("headingFieldSet", { name })}</h2>
      <p className="max-w-2xl">{t("fieldSetNote")}</p>
      <details className="rounded-lg border border-line p-3">
        <summary className="cursor-pointer font-semibold">{t("detailsSummary")}</summary>
        <div className="mt-3"><FormDetailsPanel kind="fieldSet" initial={set} csrfToken={session.data.csrfToken} /></div>
      </details>
      <FormBuilder kind="fieldSet" initial={set} csrfToken={session.data.csrfToken} fieldSets={[]} locations={choices.locations} previewMessages={messages} />
    </section>
  );
}

import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormBuilder } from "@/components/forms/FormBuilder";
import { FormDetailsPanel } from "@/components/forms/FormsPanel";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { loadBuilderChoices, previewMessages } from "@/lib/forms/pages";
import type { FormDetail } from "@/lib/forms/types";
import { requireSignedIn } from "@/lib/signed-in";

const isId = (value: string) => /^[0-9a-f-]{36}$/i.test(value);

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/forms/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "forms.builder" });
  return { title: t("pageTitle") };
}

/** One form in the drag-and-drop builder (task C1, module 2). */
export default async function FormBuilderPage({ params }: PageProps<"/[locale]/settings/forms/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/forms/${id}`);
  const [t, tSettings] = await Promise.all([getTranslations({ locale, namespace: "forms.builder" }), getTranslations({ locale, namespace: "settings" })]);
  if (!user?.permissions.includes("forms.manage")) {
    return <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [form, choices, messages] = await Promise.all([getJsonAs<FormDetail>(`/api/v1/forms/${id}`, token, tenant), loadBuilderChoices(token, tenant), previewMessages()]);
  if (!form) notFound();
  const name = form.name[locale as "en" | "ar"] ?? form.name.en ?? form.name.ar ?? form.key;

  return (
    <section aria-labelledby="builder-heading" className="space-y-6">
      <Link href="/settings/forms" className="text-sm font-semibold text-brand underline">{t("back")}</Link>
      <h2 id="builder-heading" className="text-2xl font-bold">{t("heading", { name })}</h2>
      <details className="rounded-lg border border-line p-3">
        <summary className="cursor-pointer font-semibold">{t("detailsSummary")}</summary>
        <div className="mt-3"><FormDetailsPanel kind="form" initial={form} csrfToken={session.data.csrfToken} /></div>
      </details>
      <FormBuilder kind="form" initial={form} csrfToken={session.data.csrfToken} fieldSets={choices.fieldSets} locations={choices.locations} previewMessages={messages} />
    </section>
  );
}

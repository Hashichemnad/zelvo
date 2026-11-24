import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormBindingsPanel, FormsPanel } from "@/components/forms/FormsPanel";
import { getJsonAs } from "@/lib/api";
import type { TreeNode } from "@/lib/directory";
import type { FormBinding, FormSummary } from "@/lib/forms/types";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/forms">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.forms" });
  return { title: t("title") };
}

/** Settings → Forms (task C1, module 2): the tenant's forms and which category uses which form. */
export default async function FormsPage({ params }: PageProps<"/[locale]/settings/forms">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/forms`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "forms.list" }),
    getTranslations({ locale, namespace: "settings.sections.forms" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const token = session.data.accessToken;
  const allowed = user?.permissions.includes("forms.manage");
  const [forms, bindings, categories] = allowed
    ? await Promise.all([
        getJsonAs<FormSummary[]>("/api/v1/forms", token, tenant),
        getJsonAs<FormBinding[]>("/api/v1/form-bindings", token, tenant),
        getJsonAs<TreeNode[]>("/api/v1/categories", token, tenant),
      ])
    : [null, null, null];

  return (
    <section aria-labelledby="forms-heading" className="space-y-6">
      <h2 id="forms-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!forms || !bindings ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <FormsPanel initial={forms} csrfToken={session.data.csrfToken} />
          <section aria-labelledby="bindings-heading" className="space-y-3">
            <h3 id="bindings-heading" className="text-xl font-semibold">{t("bindingsTitle")}</h3>
            <FormBindingsPanel initial={bindings} forms={forms} categories={categories ?? []} csrfToken={session.data.csrfToken} />
          </section>
        </>
      )}
    </section>
  );
}

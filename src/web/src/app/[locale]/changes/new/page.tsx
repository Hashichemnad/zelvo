import { getTranslations, setRequestLocale } from "next-intl/server";
import { NewChangeForm } from "@/components/changes/NewChangeForm";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { ChangeTemplate } from "@/lib/changes";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/changes/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "changes.new" });
  return { title: t("title") };
}

/** Raise a change (changes.edit): from the catalog of standard changes, or a normal or emergency change; `?problem=` links the problem it fixes. */
export default async function NewChangePage({ params, searchParams }: PageProps<"/[locale]/changes/new">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/changes/new`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "changes.new" }), getTranslations({ locale, namespace: "changes.list" })]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("changes.edit")) {
    return <p role="alert">{tList("noAccess")}</p>;
  }
  const raw = (await searchParams).problem;
  const problem = typeof raw === "string" && /^[A-Z][A-Z0-9]{0,9}-\d{1,12}$/.test(raw) ? raw : null;
  const token = session.data.accessToken;
  const [choices, templates] = await Promise.all([
    loadDirectoryChoices(token, tenant, permissions),
    getJsonAs<ChangeTemplate[]>("/api/v1/change-templates", token, tenant),
  ]);

  return (
    <section aria-labelledby="new-change-heading" className="space-y-6">
      <Link href="/changes" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{tList("title")}</Link>
      <h1 id="new-change-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      <NewChangeForm templates={templates ?? []} categories={choices.categories} csrfToken={session.data.csrfToken} problemNumber={problem} />
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { NewProblemForm } from "@/components/problems/NewProblemForm";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/problems/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "problems.new" });
  return { title: t("title") };
}

/** Raise a problem (problems.edit), from the incidents in `?incidents=` (ids, comma-separated) or by hand. */
export default async function NewProblemPage({ params, searchParams }: PageProps<"/[locale]/problems/new">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/problems/new`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "problems.new" }), getTranslations({ locale, namespace: "problems.list" })]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("problems.edit")) {
    return <p role="alert">{tList("noAccess")}</p>;
  }
  const raw = (await searchParams).incidents;
  const ids = (typeof raw === "string" ? raw.split(",") : []).filter(isId).slice(0, 100);
  const token = session.data.accessToken;
  const [choices, incidents] = await Promise.all([
    loadDirectoryChoices(token, tenant, permissions),
    Promise.all(ids.map((id) => getJsonAs<{ id: string; number: string }>(`/api/v1/incidents/${id}`, token, tenant))),
  ]);
  const found = incidents.filter((i): i is { id: string; number: string } => i !== null);

  return (
    <section aria-labelledby="new-problem-heading" className="space-y-6">
      <Link href="/problems" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{tList("title")}</Link>
      <h1 id="new-problem-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      <NewProblemForm incidentIds={found.map((i) => i.id)} incidentNumbers={found.map((i) => i.number)} categories={choices.categories} csrfToken={session.data.csrfToken} />
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { Pagination } from "@/components/incidents/IncidentTable";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { KnownError } from "@/lib/problems";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/known-errors">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "problems.knownErrors" });
  return { title: t("title") };
}

const fieldClass =
  "block max-w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

/** The known error database (task H1, problems.read): search in both languages and the status. */
export default async function KnownErrorsPage({ params, searchParams }: PageProps<"/[locale]/known-errors">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/known-errors`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "problems.knownErrors" }), getTranslations({ locale, namespace: "problems.list" })]);
  if (!(user?.permissions ?? []).includes("problems.read")) {
    return <p role="alert">{user ? tList("noAccess") : tList("unavailable")}</p>;
  }
  const raw = await searchParams;
  const value = (key: string) => (typeof raw[key] === "string" ? (raw[key] as string) : "");
  const query: Record<string, string> = {};
  const q = value("q").slice(0, 200);
  if (q) query.q = q;
  if (["Active", "Retired"].includes(value("status"))) query.status = value("status");
  const pageNumber = Number(value("page"));
  const page = Number.isInteger(pageNumber) && pageNumber > 0 && pageNumber < 10000 ? pageNumber : 1;
  const errors = await getJsonAs<{ items: KnownError[]; page: number; pageSize: number; totalCount: number }>(
    `/api/v1/known-errors?${new URLSearchParams({ ...query, page: String(page), pageSize: "25" })}`, session.data.accessToken, tenant);
  const l = locale as Locale;

  return (
    <section aria-labelledby="ke-heading" className="space-y-6">
      <Link href="/problems" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{tList("title")}</Link>
      <h1 id="ke-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      <form method="get" className="flex min-w-0 flex-wrap items-end gap-3 rounded-lg border border-line p-3">
        <label className="max-w-full space-y-1 text-sm">
          <span className="block font-semibold">{t("search")}</span>
          <input type="search" name="q" defaultValue={q} maxLength={200} dir="auto" className={`${fieldClass} w-64`} />
        </label>
        <label className="max-w-full space-y-1 text-sm">
          <span className="block font-semibold">{t("status")}</span>
          <select name="status" defaultValue={query.status ?? ""} className={fieldClass}>
            <option value="">{t("anyStatus")}</option>
            <option value="Active">{t("statuses.Active")}</option>
            <option value="Retired">{t("statuses.Retired")}</option>
          </select>
        </label>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("apply")}</button>
      </form>
      {!errors ? <p role="alert">{tList("unavailable")}</p> : (
        <>
          <p role="status">{t("count", { count: errors.totalCount })}</p>
          {errors.items.length > 0 && (
            <div className="overflow-x-auto">
              <table className={tableClass} data-testid="known-error-table">
                <caption className="sr-only">{t("title")}</caption>
                <thead>
                  <tr>
                    <th scope="col" className={cellClass}>{tList("number")}</th>
                    <th scope="col" className={cellClass}>{tList("titleColumn")}</th>
                    <th scope="col" className={cellClass}>{t("status")}</th>
                    <th scope="col" className={cellClass}>{t("problem")}</th>
                  </tr>
                </thead>
                <tbody>
                  {errors.items.map((e) => {
                    const title = inLanguage(e.title, l);
                    return (
                      <tr key={e.id}>
                        <td className={cellClass} dir="ltr">{e.number}</td>
                        <td className={cellClass}><span lang={title.lang} dir={title.dir}>{title.value}</span></td>
                        <td className={cellClass}>{t(`statuses.${e.status}`)}</td>
                        <td className={cellClass}>
                          <Link href={`/problems/${e.problemId}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{e.problemNumber}</Link>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
          <Pagination page={errors.page} pageSize={errors.pageSize} totalCount={errors.totalCount} query={query} basePath="/known-errors" />
        </>
      )}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { Localized } from "@/components/directory/common";
import { Pagination } from "@/components/incidents/IncidentTable";
import { ProblemStageBadge } from "@/components/problems/common";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { Locale } from "@/lib/incidents";
import { problemStages, type ProblemPage, type ProblemSuggestionPage } from "@/lib/problems";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/problems">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "problems.list" });
  return { title: t("title") };
}

const selectClass =
  "block max-w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";
const labelClass = "max-w-full space-y-1 text-sm";

/** Problems for staff (task H1, problems.read): filters by stage, open and assignee. */
export default async function ProblemsPage({ params, searchParams }: PageProps<"/[locale]/problems">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/problems`);
  const [t, tStages] = await Promise.all([getTranslations({ locale, namespace: "problems.list" }), getTranslations({ locale, namespace: "problems.stages" })]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("problems.read")) {
    return (
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>
      </section>
    );
  }

  const raw = await searchParams;
  const value = (key: string) => (typeof raw[key] === "string" ? (raw[key] as string) : "");
  const query: Record<string, string> = {};
  if ((problemStages as readonly string[]).includes(value("stage"))) query.stage = value("stage");
  if (value("open") === "true") query.open = "true";
  if (["me", "none"].includes(value("assignee"))) query.assignee = value("assignee");
  const pageNumber = Number(value("page"));
  const page = Number.isInteger(pageNumber) && pageNumber > 0 && pageNumber < 10000 ? pageNumber : 1;
  const canManage = permissions.includes("problems.manage");
  const [problems, suggestions] = await Promise.all([
    getJsonAs<ProblemPage>(`/api/v1/problems?${new URLSearchParams({ ...query, page: String(page), pageSize: "25" })}`, session.data.accessToken, tenant),
    canManage ? getJsonAs<ProblemSuggestionPage>("/api/v1/problem-suggestions?pageSize=1", session.data.accessToken, tenant) : Promise.resolve(null),
  ]);
  const l = locale as Locale;

  return (
    <section aria-labelledby="problems-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="problems-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        <div className="flex flex-wrap gap-3">
          {canManage && (
            <Link href="/problems/suggestions" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">
              {t("suggestions", { count: suggestions?.totalCount ?? 0 })}
            </Link>
          )}
          <Link href="/known-errors" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">{t("knownErrors")}</Link>
          {permissions.includes("problems.edit") && (
            <Link href="/problems/new" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("new")}</Link>
          )}
        </div>
      </div>
      <p className="max-w-3xl">{t("intro")}</p>
      <form method="get" aria-label={t("filters")} className="flex min-w-0 flex-wrap items-end gap-3 rounded-lg border border-line p-3">
        <label className={labelClass}>
          <span className="block font-semibold">{t("stage")}</span>
          <select name="stage" defaultValue={query.stage ?? ""} className={selectClass}>
            <option value="">{t("anyStage")}</option>
            {problemStages.map((s) => <option key={s} value={s}>{tStages(s)}</option>)}
          </select>
        </label>
        <label className={labelClass}>
          <span className="block font-semibold">{t("assignee")}</span>
          <select name="assignee" defaultValue={query.assignee ?? ""} className={selectClass}>
            <option value="">{t("anyone")}</option>
            <option value="me">{t("me")}</option>
            <option value="none">{t("nobody")}</option>
          </select>
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" name="open" value="true" defaultChecked={query.open === "true"} className="size-5 accent-brand" />
          {t("openOnly")}
        </label>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("apply")}</button>
        <Link href="/problems" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">{t("clear")}</Link>
      </form>
      {!problems ? (
        <p role="alert">{t("unavailable")}</p>
      ) : (
        <>
          <p role="status">{t("count", { count: problems.totalCount })}</p>
          {problems.items.length > 0 && (
            <div className="overflow-x-auto">
              <table className={tableClass} data-testid="problem-table">
                <caption className="sr-only">{t("title")}</caption>
                <thead>
                  <tr>
                    <th scope="col" className={cellClass}>{t("number")}</th>
                    <th scope="col" className={cellClass}>{t("titleColumn")}</th>
                    <th scope="col" className={cellClass}>{t("stage")}</th>
                    <th scope="col" className={cellClass}>{t("priority")}</th>
                    <th scope="col" className={cellClass}>{t("incidents")}</th>
                    <th scope="col" className={cellClass}>{t("group")}</th>
                    <th scope="col" className={cellClass}>{t("assignee")}</th>
                  </tr>
                </thead>
                <tbody>
                  {problems.items.map((p) => (
                    <tr key={p.id}>
                      <td className={cellClass}>
                        <Link href={`/problems/${p.id}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{p.number}</Link>
                      </td>
                      <td className={cellClass} dir="auto">{p.title}</td>
                      <td className={cellClass}><ProblemStageBadge stage={p.stage} /></td>
                      <td className={cellClass} dir="ltr">{`P${p.priority}`}</td>
                      <td className={cellClass}>{p.incidentCount}</td>
                      <td className={cellClass}>{p.group ? <Localized text={p.group.name} locale={l} /> : "—"}</td>
                      <td className={cellClass}>{p.assignee?.displayName ?? "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pagination page={problems.page} pageSize={problems.pageSize} totalCount={problems.totalCount} query={query} basePath="/problems" />
        </>
      )}
    </section>
  );
}

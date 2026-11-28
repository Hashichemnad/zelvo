import { getTranslations, setRequestLocale } from "next-intl/server";
import { Pagination } from "@/components/incidents/IncidentTable";
import { DetectNowButton, SuggestionCard } from "@/components/problems/ProblemSuggestions";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { problemSuggestionStatuses, type ProblemSuggestionPage, type ProblemSuggestionStatus } from "@/lib/problems";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/problems/suggestions">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "problems.suggestions" });
  return { title: t("title") };
}

/**
 * Suggested problems (task H2, problems.manage): groups of similar incidents judged through the
 * AI gateway and rising trends, offered to problem managers, who raise the problem or dismiss the
 * suggestion with a reason. Zelvo never raises a problem by itself.
 */
export default async function ProblemSuggestionsPage({ params, searchParams }: PageProps<"/[locale]/problems/suggestions">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/problems/suggestions`);
  const t = await getTranslations({ locale, namespace: "problems.suggestions" });
  if (!(user?.permissions ?? []).includes("problems.manage")) {
    return (
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>
      </section>
    );
  }

  const raw = await searchParams;
  const value = (key: string) => (typeof raw[key] === "string" ? (raw[key] as string) : "");
  const status: ProblemSuggestionStatus = (problemSuggestionStatuses as readonly string[]).includes(value("status")) ? (value("status") as ProblemSuggestionStatus) : "Open";
  const pageNumber = Number(value("page"));
  const page = Number.isInteger(pageNumber) && pageNumber > 0 && pageNumber < 10000 ? pageNumber : 1;
  const [suggestions, format] = await Promise.all([
    getJsonAs<ProblemSuggestionPage>(`/api/v1/problem-suggestions?${new URLSearchParams({ status, page: String(page), pageSize: "10" })}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const display = { locale, ...format };

  return (
    <section aria-labelledby="suggestions-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="suggestions-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        <div className="flex flex-wrap items-start gap-3">
          <Link href="/problems" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
          <DetectNowButton csrfToken={session.data.csrfToken} />
        </div>
      </div>
      <p className="max-w-3xl">{t("intro")}</p>
      <nav aria-label={t("statuses")}>
        <ul className="flex flex-wrap gap-2">
          {problemSuggestionStatuses.map((s) => (
            <li key={s}>
              <Link href={s === "Open" ? "/problems/suggestions" : `/problems/suggestions?status=${s}`} aria-current={s === status ? "page" : undefined}
                className={`inline-block rounded-md border px-3 py-1 font-semibold ${s === status ? "border-brand bg-brand text-surface" : "border-line text-brand"}`}>
                {t(`status.${s}`)}
              </Link>
            </li>
          ))}
        </ul>
      </nav>
      {!suggestions ? (
        <p role="alert">{t("unavailable")}</p>
      ) : (
        <>
          <p role="status">{t("count", { count: suggestions.totalCount })}</p>
          <div className="space-y-4">
            {suggestions.items.map((s) => <SuggestionCard key={s.id} suggestion={s} display={display} csrfToken={session.data.csrfToken} />)}
          </div>
          <Pagination page={suggestions.page} pageSize={suggestions.pageSize} totalCount={suggestions.totalCount}
            query={status === "Open" ? {} : { status }} basePath="/problems/suggestions" />
        </>
      )}
    </section>
  );
}

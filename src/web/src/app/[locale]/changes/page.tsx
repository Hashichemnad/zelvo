import { getTranslations, setRequestLocale } from "next-intl/server";
import { ChangeStageBadge, RiskBadge } from "@/components/changes/common";
import { Localized } from "@/components/directory/common";
import { Pagination } from "@/components/incidents/IncidentTable";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { changeKinds, changeStages, riskLevels, type ChangePage } from "@/lib/changes";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/changes">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "changes.list" });
  return { title: t("title") };
}

const selectClass =
  "block max-w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";
const labelClass = "max-w-full space-y-1 text-sm";

/** Changes for staff (task H3, changes.read): filters by stage, type, risk and assignee. */
export default async function ChangesPage({ params, searchParams }: PageProps<"/[locale]/changes">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/changes`);
  const [t, tStages, tKinds, tRisk] = await Promise.all([
    getTranslations({ locale, namespace: "changes.list" }), getTranslations({ locale, namespace: "changes.stages" }),
    getTranslations({ locale, namespace: "changes.kinds" }), getTranslations({ locale, namespace: "changes.risk" }),
  ]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("changes.read")) {
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
  if ((changeStages as readonly string[]).includes(value("stage"))) query.stage = value("stage");
  if ((changeKinds as readonly string[]).includes(value("kind"))) query.kind = value("kind");
  if ((riskLevels as readonly string[]).includes(value("riskLevel"))) query.riskLevel = value("riskLevel");
  if (value("open") === "true") query.open = "true";
  if (["me", "none"].includes(value("assignee"))) query.assignee = value("assignee");
  const pageNumber = Number(value("page"));
  const page = Number.isInteger(pageNumber) && pageNumber > 0 && pageNumber < 10000 ? pageNumber : 1;
  const [changes, format] = await Promise.all([
    getJsonAs<ChangePage>(`/api/v1/changes?${new URLSearchParams({ ...query, page: String(page), pageSize: "25" })}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const display = { locale, ...format };
  const l = locale as Locale;

  return (
    <section aria-labelledby="changes-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="changes-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        <div className="flex flex-wrap gap-3">
          <Link href="/changes/calendar" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">{t("calendar")}</Link>
          <Link href="/changes/board" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">{t("board")}</Link>
          {permissions.includes("changes.edit") && (
            <Link href="/changes/new" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("new")}</Link>
          )}
        </div>
      </div>
      <p className="max-w-3xl">{t("intro")}</p>
      <form method="get" aria-label={t("filters")} className="flex min-w-0 flex-wrap items-end gap-3 rounded-lg border border-line p-3">
        <label className={labelClass}>
          <span className="block font-semibold">{t("stage")}</span>
          <select name="stage" defaultValue={query.stage ?? ""} className={selectClass}>
            <option value="">{t("anyStage")}</option>
            {changeStages.map((s) => <option key={s} value={s}>{tStages(s)}</option>)}
          </select>
        </label>
        <label className={labelClass}>
          <span className="block font-semibold">{t("kind")}</span>
          <select name="kind" defaultValue={query.kind ?? ""} className={selectClass}>
            <option value="">{t("anyKind")}</option>
            {changeKinds.map((k) => <option key={k} value={k}>{tKinds(k)}</option>)}
          </select>
        </label>
        <label className={labelClass}>
          <span className="block font-semibold">{t("risk")}</span>
          <select name="riskLevel" defaultValue={query.riskLevel ?? ""} className={selectClass}>
            <option value="">{t("anyRisk")}</option>
            {riskLevels.map((r) => <option key={r} value={r}>{tRisk(`levels.${r}`)}</option>)}
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
        <Link href="/changes" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">{t("clear")}</Link>
      </form>
      {!changes ? (
        <p role="alert">{t("unavailable")}</p>
      ) : (
        <>
          <p role="status">{t("count", { count: changes.totalCount })}</p>
          {changes.items.length > 0 && (
            <div className="overflow-x-auto">
              <table className={tableClass} data-testid="change-table">
                <caption className="sr-only">{t("title")}</caption>
                <thead>
                  <tr>
                    <th scope="col" className={cellClass}>{t("number")}</th>
                    <th scope="col" className={cellClass}>{t("titleColumn")}</th>
                    <th scope="col" className={cellClass}>{t("kind")}</th>
                    <th scope="col" className={cellClass}>{t("stage")}</th>
                    <th scope="col" className={cellClass}>{t("risk")}</th>
                    <th scope="col" className={cellClass}>{t("window")}</th>
                    <th scope="col" className={cellClass}>{t("group")}</th>
                    <th scope="col" className={cellClass}>{t("assignee")}</th>
                  </tr>
                </thead>
                <tbody>
                  {changes.items.map((c) => (
                    <tr key={c.id}>
                      <td className={cellClass}>
                        <Link href={`/changes/${c.id}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{c.number}</Link>
                      </td>
                      <td className={cellClass} dir="auto">{c.title}</td>
                      <td className={cellClass}>{tKinds(c.kind)}</td>
                      <td className={cellClass}><ChangeStageBadge stage={c.stage} /></td>
                      <td className={cellClass}><RiskBadge level={c.riskLevel} /></td>
                      <td className={cellClass}>{c.plannedStart ? formatDateTime(new Date(c.plannedStart), display) : "—"}</td>
                      <td className={cellClass}>{c.group ? <Localized text={c.group.name} locale={l} /> : "—"}</td>
                      <td className={cellClass}>{c.assignee?.displayName ?? "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pagination page={changes.page} pageSize={changes.pageSize} totalCount={changes.totalCount} query={query} basePath="/changes" />
        </>
      )}
    </section>
  );
}

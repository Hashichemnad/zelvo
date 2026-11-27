import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { Localized } from "@/components/directory/common";
import { Pagination } from "@/components/incidents/IncidentTable";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { RecordPage, RecordType } from "@/lib/records";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/records/[type]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "records.index" });
  return { title: t("title") };
}

const selectClass =
  "block max-w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

/** Records of one custom type: all of them for staff with its read permission, otherwise the person's own. */
export default async function RecordListPage({ params, searchParams }: PageProps<"/[locale]/records/[type]">) {
  const { locale, type: key } = await params;
  setRequestLocale(locale);
  if (!/^[a-z][a-z0-9-]{0,39}$/.test(key)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/records/${key}`);
  const t = await getTranslations({ locale, namespace: "records.list" });
  const token = session.data.accessToken;
  const type = await getJsonAs<RecordType>(`/api/v1/record-types/${key}`, token, tenant);
  if (!type) notFound();
  const raw = await searchParams;
  const value = (k: string) => (typeof raw[k] === "string" ? (raw[k] as string) : "");
  const query: Record<string, string> = {};
  if (type.states.some((s) => s.key === value("state"))) query.state = value("state");
  if (value("open") === "true") query.open = "true";
  if (type.access.read && ["me", "none"].includes(value("assignee"))) query.assignee = value("assignee");
  const pageNumber = Number(value("page"));
  const page = Number.isInteger(pageNumber) && pageNumber > 0 && pageNumber < 10000 ? pageNumber : 1;
  const records = await getJsonAs<RecordPage>(`/api/v1/records/${key}?${new URLSearchParams({ ...query, page: String(page), pageSize: "25" })}`, token, tenant);
  const l = locale as Locale;
  const name = inLanguage(type.name, l);

  return (
    <section aria-labelledby="record-list-heading" className="space-y-6">
      <Link href="/records" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="record-list-heading" className="text-3xl font-bold text-brand"><span lang={name.lang} dir={name.dir}>{name.value}</span></h1>
        {type.access.raise && type.active && <Link href={`/records/${key}/new`} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("raise")}</Link>}
      </div>
      <form method="get" aria-label={t("filters")} className="flex min-w-0 flex-wrap items-end gap-3 rounded-lg border border-line p-3">
        <label className="max-w-full space-y-1 text-sm">
          <span className="block font-semibold">{t("state")}</span>
          <select name="state" defaultValue={query.state ?? ""} className={selectClass}>
            <option value="">{t("anyState")}</option>
            {type.states.map((s) => <option key={s.key} value={s.key}>{inLanguage(s.name, l).value}</option>)}
          </select>
        </label>
        {type.access.read && (
          <label className="max-w-full space-y-1 text-sm">
            <span className="block font-semibold">{t("assignee")}</span>
            <select name="assignee" defaultValue={query.assignee ?? ""} className={selectClass}>
              <option value="">{t("anyone")}</option>
              <option value="me">{t("me")}</option>
              <option value="none">{t("nobody")}</option>
            </select>
          </label>
        )}
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" name="open" value="true" defaultChecked={query.open === "true"} className="size-5 accent-brand" />
          {t("openOnly")}
        </label>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("apply")}</button>
      </form>
      {!records ? <p role="alert">{t("unavailable")}</p> : (
        <>
          <p role="status">{t("count", { count: records.totalCount })}</p>
          {records.items.length > 0 && (
            <div className="overflow-x-auto">
              <table className={tableClass} data-testid="record-table">
                <caption className="sr-only">{name.value}</caption>
                <thead>
                  <tr>
                    <th scope="col" className={cellClass}>{t("number")}</th>
                    <th scope="col" className={cellClass}>{t("titleColumn")}</th>
                    <th scope="col" className={cellClass}>{t("state")}</th>
                    <th scope="col" className={cellClass}>{t("group")}</th>
                    {type.access.read && <th scope="col" className={cellClass}>{t("assignee")}</th>}
                  </tr>
                </thead>
                <tbody>
                  {records.items.map((r) => (
                    <tr key={r.id}>
                      <td className={cellClass}>
                        <Link href={`/records/${key}/${r.id}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{r.number}</Link>
                      </td>
                      <td className={cellClass} dir="auto">{r.title}</td>
                      <td className={cellClass}><Localized text={r.stateOfType.name} locale={l} /></td>
                      <td className={cellClass}>{r.group ? <Localized text={r.group.name} locale={l} /> : "—"}</td>
                      {type.access.read && <td className={cellClass}>{r.assignee?.displayName ?? "—"}</td>}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pagination page={records.page} pageSize={records.pageSize} totalCount={records.totalCount} query={query} basePath={`/records/${key}`} />
        </>
      )}
    </section>
  );
}

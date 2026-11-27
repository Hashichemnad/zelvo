import { getTranslations, setRequestLocale } from "next-intl/server";
import { AssetTable } from "@/components/assets/AssetTable";
import { AssetsNav } from "@/components/assets/AssetsNav";
import { Pagination } from "@/components/incidents/IncidentTable";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { assetStates, type AssetPage, type AssetSettings } from "@/lib/assets";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/assets">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "assets.list" });
  return { title: t("title") };
}

const selectClass =
  "block max-w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

/** Assets for staff (task G3, assets.read): search, state and warranty filters, and label sheets. */
export default async function AssetsPage({ params, searchParams }: PageProps<"/[locale]/assets">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/assets`);
  const [t, tStates] = await Promise.all([getTranslations({ locale, namespace: "assets.list" }), getTranslations({ locale, namespace: "assets.states" })]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("assets.read") && !permissions.includes("assets.manage")) {
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
  const q = value("q").slice(0, 200);
  if (q) query.q = q;
  if ((assetStates as readonly string[]).includes(value("state"))) query.state = value("state");
  if (value("warrantyEnding") === "true") query.warrantyEnding = "true";
  const pageNumber = Number(value("page"));
  const page = Number.isInteger(pageNumber) && pageNumber > 0 && pageNumber < 10000 ? pageNumber : 1;
  const token = session.data.accessToken;
  const [assets, settings, format] = await Promise.all([
    getJsonAs<AssetPage>(`/api/v1/assets?${new URLSearchParams({ ...query, page: String(page), pageSize: "25" })}`, token, tenant),
    getJsonAs<AssetSettings>("/api/v1/assets/settings", token, tenant),
    getDisplayFormat(),
  ]);

  return (
    <section aria-labelledby="assets-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="assets-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        {permissions.includes("assets.manage") && <Link href="/assets/new" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("new")}</Link>}
      </div>
      <AssetsNav current="assets" />
      <p className="max-w-3xl">{t("intro")}</p>
      <form method="get" aria-label={t("filters")} className="flex min-w-0 flex-wrap items-end gap-3 rounded-lg border border-line p-3">
        <label className="max-w-full space-y-1 text-sm">
          <span className="block font-semibold">{t("search")}</span>
          <input type="search" name="q" defaultValue={q} maxLength={200} dir="auto" className={`${selectClass} w-64`} />
        </label>
        <label className="max-w-full space-y-1 text-sm">
          <span className="block font-semibold">{t("state")}</span>
          <select name="state" defaultValue={query.state ?? ""} className={selectClass}>
            <option value="">{t("anyState")}</option>
            {assetStates.map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
          </select>
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" name="warrantyEnding" value="true" defaultChecked={query.warrantyEnding === "true"} className="size-5 accent-brand" />
          {t("warrantyEnding")}
        </label>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("apply")}</button>
        <Link href="/assets" className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">{t("clear")}</Link>
      </form>
      {!assets ? (
        <p role="alert">{t("unavailable")}</p>
      ) : (
        <>
          <p role="status">{t("count", { count: assets.totalCount })}</p>
          {assets.items.length > 0 && (
            <AssetTable items={assets.items} csrfToken={session.data.csrfToken} display={{ locale, ...format }} warningDays={settings?.expiryWarningDays ?? 30} />
          )}
          <Pagination page={assets.page} pageSize={assets.pageSize} totalCount={assets.totalCount} query={query} basePath="/assets" />
        </>
      )}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { ApprovalList } from "@/components/workflows/Approvals";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";
import type { ApprovalPage } from "@/lib/workflows";

export async function generateMetadata({ params }: PageProps<"/[locale]/approvals">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "approvals" });
  return { title: t("title") };
}

/** My approvals (task C3): decisions waiting for the signed-in person, then those already made. */
export default async function ApprovalsPage({ params }: PageProps<"/[locale]/approvals">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/approvals`);
  const t = await getTranslations({ locale, namespace: "approvals" });
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [pending, all, format] = await Promise.all([
    get<ApprovalPage>("/api/v1/approvals?state=Pending&pageSize=100"),
    get<ApprovalPage>("/api/v1/approvals?pageSize=50"),
    getDisplayFormat(),
  ]);
  const display = { locale, ...format };
  return (
    <div className="space-y-8">
      <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
      {!pending || !all ? (
        <p role="alert">{t("unavailable")}</p>
      ) : (
        <>
          <section aria-labelledby="approvals-waiting" className="space-y-2">
            <h2 id="approvals-waiting" className="text-xl font-semibold">{t("waiting", { count: pending.totalCount })}</h2>
            <ApprovalList approvals={pending.items} display={display} />
          </section>
          <section aria-labelledby="approvals-decided" className="space-y-2">
            <h2 id="approvals-decided" className="text-xl font-semibold">{t("history")}</h2>
            <ApprovalList approvals={all.items.filter((a) => a.state !== "Pending")} display={display} />
          </section>
        </>
      )}
    </div>
  );
}

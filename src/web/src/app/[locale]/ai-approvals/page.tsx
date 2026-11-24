import { getTranslations, setRequestLocale } from "next-intl/server";
import { AiApprovalsList } from "@/components/ai/AiApprovalsList";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { Page } from "@/lib/integrations";
import { canDecideAiActions, type McpAction } from "@/lib/mcp";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/ai-approvals">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "aiApprovals" });
  return { title: t("title") };
}

/** AI approvals (module 9, task E6): changes AI agents asked for, waiting for a person. */
export default async function AiApprovalsPage({ params }: PageProps<"/[locale]/ai-approvals">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/ai-approvals`);
  const t = await getTranslations({ locale, namespace: "aiApprovals" });
  if (!canDecideAiActions(user?.permissions ?? [])) {
    return <p role="alert">{t("noAccess")}</p>;
  }
  const [page, display] = await Promise.all([
    getJsonAs<Page<McpAction>>("/api/v1/ai/approvals?pageSize=50", session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="ai-approvals-heading" className="space-y-6">
      <h1 id="ai-approvals-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      {page ? <AiApprovalsList initial={page} csrfToken={session.data.csrfToken} display={{ locale, ...display }} /> : <p role="alert">{t("unavailable")}</p>}
    </section>
  );
}

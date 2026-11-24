import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { Localized } from "@/components/directory/common";
import { ApprovalDecisionForm, ApprovalProcessTimeline } from "@/components/workflows/Approvals";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { TicketFormView } from "@/lib/forms/types";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";
import type { ApprovalDetail } from "@/lib/workflows";

export async function generateMetadata({ params }: PageProps<"/[locale]/approvals/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "approvals" });
  return { title: t("detailTitle") };
}

/**
 * One approval (task C3). The approval email's buttons link here with ?decision=approve or
 * ?decision=reject; signing in is required first (product owner decision 2026-11-24).
 */
export default async function ApprovalPage({ params, searchParams }: PageProps<"/[locale]/approvals/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const query = await searchParams;
  const decision = typeof query.decision === "string" ? query.decision : null;
  const path = `/${locale}/approvals/${id}${decision ? `?decision=${decision === "reject" ? "reject" : "approve"}` : ""}`;
  const { session, tenant } = await requireSignedIn(locale, path);
  const t = await getTranslations({ locale, namespace: "approvals" });
  const [detail, format] = await Promise.all([
    getJsonAs<ApprovalDetail>(`/api/v1/approvals/${id}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const display = { locale, ...format };
  if (!detail) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/approvals" className="font-semibold text-brand underline">{t("back")}</Link>
      </section>
    );
  }
  const ticket = detail.approval.ticket;
  return (
    <article aria-labelledby="approval-heading" className="space-y-6">
      <Link href="/approvals" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{ticket.number}</p>
        <h1 id="approval-heading" className="text-3xl font-bold text-brand" dir="auto">{ticket.title}</h1>
        <p>
          {t("askedBy", { name: ticket.requester.displayName ?? "—" })}
          {ticket.service && <> · <Localized text={ticket.service.name} locale={locale as "en" | "ar"} /></>}
        </p>
      </header>
      {detail.description && <p className="whitespace-pre-wrap" dir="auto">{detail.description}</p>}
      {detail.form ? <FormAnswersView view={detail.form as TicketFormView} attachmentBase={`/bff/api/v1/service-requests/${ticket.id}/attachments`} /> : null}
      <ApprovalDecisionForm detail={detail} csrfToken={session.data.csrfToken} fromEmail={decision !== null}
        initialChoice={decision === "approve" ? "Approve" : decision === "reject" ? "Reject" : null} />
      <ApprovalProcessTimeline process={detail.process} display={display} headingLevel={2} />
    </article>
  );
}

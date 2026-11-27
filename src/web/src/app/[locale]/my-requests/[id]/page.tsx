import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { BundleLink, BundleParts, ReopenRequestButton, RequestAttachments, RequestFacts, RequestProgress } from "@/components/catalog/ServiceRequests";
import { TicketRatingBlock } from "@/components/voice/TicketRatingBlock";
import { ExportPdfButton } from "@/components/ExportPdfButton";
import { CancelApprovalButton, RequestApprovals } from "@/components/workflows/Approvals";
import { PaymentPanel } from "@/components/payments/PaymentPanel";
import { Link } from "@/i18n/navigation";
import { isId } from "@/lib/incident-pages";
import { loadRequest } from "@/lib/request-pages";
import { requireSignedIn } from "@/lib/signed-in";
import { getAppInfo } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { RememberTicket } from "@/components/pwa/RememberTicket";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-requests/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.requests" });
  return { title: t("myTitle") };
}

/** One of the user's service requests: where it is in the service's steps, answers, files, reopen. */
export default async function MyRequestPage({ params }: PageProps<"/[locale]/my-requests/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-requests/${id}`);
  const t = await getTranslations({ locale, namespace: "catalog.requests" });
  const tApprovals = await getTranslations({ locale, namespace: "approvals" });
  const { request, settings, attachments, form, display, documentNames, approvals, payments } = await loadRequest(id, session.data.accessToken, tenant, locale);
  if (!request) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/my-requests" className="font-semibold text-brand underline">{t("backToMine")}</Link>
      </section>
    );
  }
  // Task J1: kept on the device for the offline page, as the requester sees it now.
  const app = await getAppInfo(tenant);
  const tStates = await getTranslations({ locale, namespace: "incidents.states" });
  const offline = {
    id: request.id,
    number: request.number,
    title: request.title,
    state: tStates(request.state),
    team: null,
    description: request.description,
    href: `/${locale}/my-requests/${request.id}`,
    messages: [],
    updated: formatDateTime(new Date(request.updatedAt), display),
  };
  return (
    <article aria-labelledby="request-heading" className="space-y-8">
      <RememberTicket ticket={offline} enabled={app?.offlineData ?? true} />
      <Link href="/my-requests" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("backToMine")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{request.number}</p>
        <h1 id="request-heading" className="text-3xl font-bold text-brand" dir="auto">{request.title}</h1>
      </header>
      {/* Task F1: a branded PDF in the page's language. */}
      <ExportPdfButton path={`/service-requests/${request.id}`} csrfToken={session.data.csrfToken} />
      {request.awaitingApproval && <p role="status" className="rounded-md border border-line bg-surface-muted p-3 font-semibold">{tApprovals("awaitingRequester")}</p>}
      <BundleLink request={request} basePath="/my-requests" />
      <RequestProgress request={request} />
      <BundleParts request={request} basePath="/my-requests" />
      <PaymentPanel payments={payments} mayPay={true} mayRefund={false} display={display} csrfToken={session.data.csrfToken} />
      <RequestFacts request={request} display={display} settings={settings} basePath="/my-requests" />
      {form && <FormAnswersView view={form} attachmentBase={`/bff/api/v1/service-requests/${request.id}/attachments`} />}
      <RequestAttachments requestId={request.id} attachments={attachments} documentNames={documentNames} />
      <RequestApprovals processes={approvals} display={display} />
      {request.awaitingApproval && approvals.some((p) => p.state === "Pending" && p.stage === "BeforeAssignment") && (
        <CancelApprovalButton requestId={request.id} csrfToken={session.data.csrfToken} requester />
      )}
      {(request.state === "Resolved" || request.state === "Closed") && (
        <TicketRatingBlock ticketId={request.id} accessToken={session.data.accessToken} tenant={tenant} csrfToken={session.data.csrfToken} />
      )}
      {request.nextStates.includes("Reopened") && <ReopenRequestButton requestId={request.id} etag={request.etag} csrfToken={session.data.csrfToken} />}
    </article>
  );
}

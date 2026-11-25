import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { RequestAttachments, RequestFacts, RequestProgress, RequestWorkPanel } from "@/components/catalog/ServiceRequests";
import { SlaClocks } from "@/components/sla/SlaClocks";
import { ExportPdfButton } from "@/components/ExportPdfButton";
import { CancelApprovalButton, RequestApprovals } from "@/components/workflows/Approvals";
import { RequestTasksPanel } from "@/components/catalog/RequestTasks";
import type { RequestTask } from "@/lib/catalog";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { GroupSummary } from "@/lib/directory";
import { isId } from "@/lib/incident-pages";
import type { Person } from "@/lib/incidents";
import { loadRequest } from "@/lib/request-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/requests/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.requests" });
  return { title: t("title") };
}

/** One service request for staff: progress, facts, answers, files, SLA clocks, assignment and state. */
export default async function RequestPage({ params }: PageProps<"/[locale]/requests/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/requests/${id}`);
  const t = await getTranslations({ locale, namespace: "catalog.requests" });
  const tApprovals = await getTranslations({ locale, namespace: "approvals" });
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("requests.read")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [data, assignees, groups, tasks] = await Promise.all([
    loadRequest(id, token, tenant, locale),
    permissions.includes("requests.assign") ? getJsonAs<Person[]>("/api/v1/service-requests/assignees", token, tenant) : Promise.resolve(null),
    permissions.includes("directory.read") ? getJsonAs<GroupSummary[]>("/api/v1/groups", token, tenant) : Promise.resolve(null),
    getJsonAs<RequestTask[]>(`/api/v1/service-requests/${encodeURIComponent(id)}/tasks`, token, tenant),
  ]);
  const { request, settings, attachments, form, display, documentNames, approvals } = data;
  if (!request) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/requests" className="font-semibold text-brand underline">{t("backToList")}</Link>
      </section>
    );
  }
  return (
    <article aria-labelledby="request-heading" className="space-y-8">
      <Link href="/requests" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("backToList")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{request.number}</p>
        <h1 id="request-heading" className="text-3xl font-bold text-brand" dir="auto">{request.title}</h1>
      </header>
      {/* Task F1: a branded PDF in the page's language. */}
      <ExportPdfButton path={`/service-requests/${request.id}`} csrfToken={session.data.csrfToken} />
      {request.awaitingApproval && <p role="status" className="rounded-md border border-line bg-surface-muted p-3 font-semibold">{tApprovals("awaitingStaff")}</p>}
      <RequestProgress request={request} />
      <RequestFacts request={request} display={display} settings={settings} basePath="/requests" />
      {request.sla.length > 0 && <SlaClocks clocks={request.sla} display={display} />}
      {form && <FormAnswersView view={form} attachmentBase={`/bff/api/v1/service-requests/${request.id}/attachments`} />}
      <RequestAttachments requestId={request.id} attachments={attachments} documentNames={documentNames} />
      <RequestTasksPanel tasks={tasks ?? []} display={display} csrfToken={session.data.csrfToken} />
      <RequestApprovals processes={approvals} display={display} />
      {request.awaitingApproval && permissions.includes("requests.edit") && (
        <CancelApprovalButton requestId={request.id} csrfToken={session.data.csrfToken} requester={false} />
      )}
      {permissions.includes("requests.edit") || permissions.includes("requests.assign") ? (
        <RequestWorkPanel request={request} settings={settings} groups={groups ?? []} assignees={assignees} permissions={permissions} csrfToken={session.data.csrfToken} />
      ) : null}
    </article>
  );
}

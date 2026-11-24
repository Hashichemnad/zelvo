import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { ReopenRequestButton, RequestAttachments, RequestFacts, RequestProgress } from "@/components/catalog/ServiceRequests";
import { ExportPdfButton } from "@/components/ExportPdfButton";
import { Link } from "@/i18n/navigation";
import { isId } from "@/lib/incident-pages";
import { loadRequest } from "@/lib/request-pages";
import { requireSignedIn } from "@/lib/signed-in";

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
  const { request, settings, attachments, form, display, documentNames } = await loadRequest(id, session.data.accessToken, tenant, locale);
  if (!request) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/my-requests" className="font-semibold text-brand underline">{t("backToMine")}</Link>
      </section>
    );
  }
  return (
    <article aria-labelledby="request-heading" className="space-y-8">
      <Link href="/my-requests" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("backToMine")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{request.number}</p>
        <h1 id="request-heading" className="text-3xl font-bold text-brand" dir="auto">{request.title}</h1>
      </header>
      {/* Task F1: a branded PDF in the page's language. */}
      <ExportPdfButton path={`/service-requests/${request.id}`} csrfToken={session.data.csrfToken} />
      <RequestProgress request={request} />
      <RequestFacts request={request} display={display} settings={settings} basePath="/my-requests" />
      {form && <FormAnswersView view={form} attachmentBase={`/bff/api/v1/service-requests/${request.id}/attachments`} />}
      <RequestAttachments requestId={request.id} attachments={attachments} documentNames={documentNames} />
      {request.nextStates.includes("Reopened") && <ReopenRequestButton requestId={request.id} etag={request.etag} csrfToken={session.data.csrfToken} />}
    </article>
  );
}

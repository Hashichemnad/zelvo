import { getTranslations, setRequestLocale } from "next-intl/server";
import { PrivacyRequestActions } from "@/components/privacy/PrivacyRequestOfficer";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";
import type { PrivacyRequest } from "@/lib/privacy";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/privacy-requests/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "privacy.officer" });
  return { title: t("detailTitle") };
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** One data subject request: the person, what Zelvo holds about them and what the officer does (task F5). */
export default async function PrivacyRequestPage({ params }: PageProps<"/[locale]/privacy-requests/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/privacy-requests/${id}`);
  const [t, tTypes, tStates] = await Promise.all([
    getTranslations({ locale, namespace: "privacy.officer" }),
    getTranslations({ locale, namespace: "privacy.requestTypes" }),
    getTranslations({ locale, namespace: "privacy.requestStates" }),
  ]);
  if (!user?.permissions.includes("privacy.requests")) {
    return <p role="alert">{t(user ? "noAccess" : "unavailable")}</p>;
  }
  const request = uuid.test(id) ? await getJsonAs<PrivacyRequest>(`/api/v1/privacy/requests/${id}`, session.data.accessToken, tenant) : null;
  if (!request) {
    return <p role="alert">{t("notFound")}</p>;
  }
  const format = await getDisplayFormat();
  const when = (iso: string | null) => (iso ? formatDateTime(new Date(iso), { locale, ...format }) : "—");
  const h = request.holdings;
  return (
    <section aria-labelledby="privacy-request-heading" className="space-y-6" data-testid="privacy-request">
      <p><Link href="/privacy-requests" className="text-brand underline">{t("back")}</Link></p>
      <h1 id="privacy-request-heading" className="text-3xl font-bold text-brand">
        <span dir="ltr">{request.number}</span> · {tTypes(request.type)}
      </h1>
      <dl className="grid gap-3 sm:grid-cols-2">
        <div><dt className="text-sm text-ink-muted">{t("fields.state")}</dt><dd className="font-semibold">{tStates(request.state)}</dd></div>
        <div><dt className="text-sm text-ink-muted">{t("fields.channel")}</dt><dd>{t(`channels.${request.channel}`)}</dd></div>
        <div><dt className="text-sm text-ink-muted">{t("fields.received")}</dt><dd>{when(request.receivedAt)}</dd></div>
        <div>
          <dt className="text-sm text-ink-muted">{t("fields.due")}</dt>
          <dd className={request.overdue ? "font-semibold text-bad" : ""}>{when(request.dueAt)}{request.overdue && ` · ${t("overdue")}`}</dd>
        </div>
        <div>
          <dt className="text-sm text-ink-muted">{t("fields.person")}</dt>
          <dd>{request.subject?.erased ? t("erasedPerson") : <>{request.subject?.name ?? "—"} <span dir="ltr" className="text-sm">{request.subject?.email}</span></>}</dd>
        </div>
        <div><dt className="text-sm text-ink-muted">{t("fields.handler")}</dt><dd>{request.handler ?? "—"}</dd></div>
        {request.identityCheck && <div className="sm:col-span-2"><dt className="text-sm text-ink-muted">{t("fields.identity")}</dt><dd>{request.identityCheck}</dd></div>}
        {request.details && <div className="sm:col-span-2"><dt className="text-sm text-ink-muted">{t("fields.details")}</dt><dd dir="auto" className="whitespace-pre-wrap">{request.details}</dd></div>}
        {request.resolution && <div className="sm:col-span-2"><dt className="text-sm text-ink-muted">{t("fields.resolution")}</dt><dd dir="auto" className="whitespace-pre-wrap">{request.resolution}</dd></div>}
        {request.exportedAt && <div><dt className="text-sm text-ink-muted">{t("fields.exported")}</dt><dd>{when(request.exportedAt)}</dd></div>}
        {request.erasedAt && <div><dt className="text-sm text-ink-muted">{t("fields.erased")}</dt><dd>{when(request.erasedAt)}</dd></div>}
      </dl>
      {h && (
        <section aria-labelledby="holdings-heading" className="space-y-2">
          <h2 id="holdings-heading" className="text-xl font-bold">{t("holdings.heading")}</h2>
          <ul className="grid gap-2 sm:grid-cols-4" data-testid="privacy-holdings">
            {(["tickets", "openTickets", "messages", "files", "chats", "notifications", "payments", "consents"] as const).map((k) => (
              <li key={k} className="rounded-md border border-line p-2"><span className="block text-sm text-ink-muted">{t(`holdings.${k}`)}</span><span className="text-lg font-semibold">{h[k]}</span></li>
            ))}
          </ul>
        </section>
      )}
      <PrivacyRequestActions initial={request} csrfToken={session.data.csrfToken} />
    </section>
  );
}

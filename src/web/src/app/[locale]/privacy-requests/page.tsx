import { getTranslations, setRequestLocale } from "next-intl/server";
import { LogRequestForm } from "@/components/privacy/PrivacyRequestOfficer";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";
import type { PrivacyRequest } from "@/lib/privacy";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/privacy-requests">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "privacy.officer" });
  return { title: t("title") };
}

const filters = ["open", "overdue", "all"] as const;

/** Data subject requests for privacy officers (GOV-5, task F5), soonest due first. */
export default async function PrivacyRequestsPage({ params, searchParams }: PageProps<"/[locale]/privacy-requests">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/privacy-requests`);
  const [t, tTypes, tStates] = await Promise.all([
    getTranslations({ locale, namespace: "privacy.officer" }),
    getTranslations({ locale, namespace: "privacy.requestTypes" }),
    getTranslations({ locale, namespace: "privacy.requestStates" }),
  ]);
  if (!user?.permissions.includes("privacy.requests")) {
    return <p role="alert">{t(user ? "noAccess" : "unavailable")}</p>;
  }
  const raw = (await searchParams).filter;
  const filter = filters.find((f) => f === raw) ?? "open";
  const query = new URLSearchParams({ pageSize: "100" });
  if (filter !== "all") query.set(filter, "true");
  const [list, format] = await Promise.all([
    getJsonAs<{ items: PrivacyRequest[]; totalCount: number }>(`/api/v1/privacy/requests?${query}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const when = (iso: string) => formatDateTime(new Date(iso), { locale, ...format });
  return (
    <section aria-labelledby="privacy-requests-heading" className="space-y-6">
      <h1 id="privacy-requests-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-2xl">{t("intro")}</p>
      <nav aria-label={t("filterLabel")}>
        <ul className="flex flex-wrap gap-3">
          {filters.map((f) => (
            <li key={f}>
              <Link href={`/privacy-requests?filter=${f}`} aria-current={f === filter ? "page" : undefined}
                className={f === filter ? "font-bold text-brand underline" : "text-brand"}>{t(`filters.${f}`)}</Link>
            </li>
          ))}
        </ul>
      </nav>
      {!list ? <p role="alert">{t("unavailable")}</p> : list.items.length === 0 ? <p>{t("none")}</p> : (
        <table className="w-full text-start text-sm" data-testid="privacy-requests">
          <thead>
            <tr>
              {["number", "type", "person", "state", "due"].map((c) => <th key={c} scope="col" className="border-b border-line py-2 text-start">{t(`columns.${c}`)}</th>)}
            </tr>
          </thead>
          <tbody>
            {list.items.map((r) => (
              <tr key={r.id}>
                <td className="border-b border-line py-2"><Link href={`/privacy-requests/${r.id}`} className="font-mono text-brand underline" dir="ltr">{r.number}</Link></td>
                <td className="border-b border-line py-2">{tTypes(r.type)}</td>
                <td className="border-b border-line py-2">{r.subject?.erased ? t("erasedPerson") : r.subject?.name ?? r.subject?.email ?? "—"}</td>
                <td className="border-b border-line py-2">{tStates(r.state)}</td>
                <td className={`border-b border-line py-2 ${r.overdue ? "font-semibold text-bad" : ""}`}>
                  {when(r.dueAt)}{r.overdue && ` · ${t("overdue")}`}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <LogRequestForm csrfToken={session.data.csrfToken} />
    </section>
  );
}

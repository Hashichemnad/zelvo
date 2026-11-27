import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { CiHistory } from "@/components/cmdb/CiHistory";
import { CiItemView } from "@/components/cmdb/CiItemView";
import { CiRelationsArea } from "@/components/cmdb/CiRelationsArea";
import { CiTickets } from "@/components/cmdb/CiTickets";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { CiClass, CiRelationshipType, CiTicket, ConfigurationItem } from "@/lib/cmdb";
import { getDisplayFormat } from "@/lib/display";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { isId } from "@/lib/incident-pages";
import type { HistoryEntry } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/cmdb/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "cmdb.list" });
  return { title: t("title") };
}

/**
 * One configuration item for staff (task G1): facts and attributes, relationships, impact, the
 * relationship map with its list view, the tickets it is on and its history.
 */
export default async function CiPage({ params }: PageProps<"/[locale]/cmdb/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/cmdb/${id}`);
  const t = await getTranslations({ locale, namespace: "cmdb.list" });
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("cmdb.read")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const get = <T,>(path: string) => getJsonAs<T>(path, token, tenant);
  const [item, classes, types, tickets, history, choices, format] = await Promise.all([
    get<ConfigurationItem>(`/api/v1/cmdb/items/${id}`),
    get<CiClass[]>("/api/v1/cmdb/classes"),
    get<CiRelationshipType[]>("/api/v1/cmdb/relationship-types"),
    get<CiTicket[]>(`/api/v1/cmdb/items/${id}/tickets`),
    get<HistoryEntry[]>(`/api/v1/cmdb/items/${id}/history`),
    loadDirectoryChoices(token, tenant, permissions),
    getDisplayFormat(),
  ]);
  if (!item) notFound();
  const display = { locale, ...format };
  const canManage = permissions.includes("cmdb.manage");

  return (
    <article aria-labelledby="ci-heading" className="space-y-8">
      <Link href="/cmdb" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <header className="space-y-1">
        <p className="font-mono text-ink-muted" dir="ltr">{item.number}</p>
        <h1 id="ci-heading" className="text-3xl font-bold text-brand" dir="auto">{item.name}</h1>
      </header>
      <CiItemView initial={item} classes={classes ?? []} groups={choices.groups} locations={choices.locations} display={display}
        canManage={canManage} csrfToken={session.data.csrfToken} />
      <CiRelationsArea itemId={item.id} types={types ?? []} classes={classes ?? []} canManage={canManage} csrfToken={session.data.csrfToken} digits={display.digits} />
      <CiTickets tickets={tickets ?? []} display={display} />
      <CiHistory entries={history ?? []} display={display} />
    </article>
  );
}

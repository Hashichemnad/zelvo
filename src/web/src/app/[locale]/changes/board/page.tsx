import { getTranslations, setRequestLocale } from "next-intl/server";
import { PlanMeetingForm } from "@/components/changes/ChangeBoard";
import { ChangeStageBadge, RiskBadge } from "@/components/changes/common";
import { Localized } from "@/components/directory/common";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { Change, ChangeBoard, MeetingSummary } from "@/lib/changes";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/changes/board">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "changes.board" });
  return { title: t("title") };
}

/**
 * The change advisory board (task H4, changes.read): who sits on the boards, the changes waiting
 * for the board, upcoming and past meetings, and planning a meeting (the change manager or the
 * board's chair).
 */
export default async function BoardPage({ params }: PageProps<"/[locale]/changes/board">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/changes/board`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "changes.board" }), getTranslations({ locale, namespace: "changes.list" })]);
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("changes.read")) {
    return <p role="alert">{user ? tList("noAccess") : tList("unavailable")}</p>;
  }
  const token = session.data.accessToken;
  const [boards, waiting, meetings, format] = await Promise.all([
    getJsonAs<ChangeBoard[]>("/api/v1/change-boards", token, tenant),
    getJsonAs<Change[]>("/api/v1/cab-meetings/waiting", token, tenant),
    getJsonAs<MeetingSummary[]>("/api/v1/cab-meetings", token, tenant),
    getDisplayFormat(),
  ]);
  const display = { locale, ...format };
  const l = locale as Locale;
  const advisory = boards?.find((b) => b.kind === "Advisory");
  const mayPlan = permissions.includes("changes.manage") || (user?.id !== undefined && (advisory?.chair?.id === user.id || advisory?.deputyChair?.id === user.id));

  return (
    <section aria-labelledby="board-heading" className="space-y-6">
      <Link href="/changes" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{tList("title")}</Link>
      <h1 id="board-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      {boards && (
        <dl className="grid gap-3 rounded-lg border border-line p-4 sm:grid-cols-2" data-testid="boards-summary">
          {boards.map((b) => (
            <div key={b.kind}>
              <dt className="font-semibold"><Localized text={b.name} locale={l} /></dt>
              <dd className="text-sm">
                {b.kind === "Advisory" && <>{t("chairIs", { name: b.chair?.displayName ?? "—" })} · </>}
                {b.kind === "Emergency" && <>{t("decidesWithin", { hours: b.decideWithinHours })} · </>}
                {t("memberCount", { count: b.members.length })}
              </dd>
            </div>
          ))}
        </dl>
      )}
      <section aria-labelledby="waiting-heading" className="space-y-2">
        <h2 id="waiting-heading" className="text-xl font-semibold">{t("waiting")}</h2>
        {!waiting || waiting.length === 0 ? <p>{t("noneWaiting")}</p> : (
          <ul className="space-y-1" data-testid="waiting-changes">
            {waiting.map((c) => (
              <li key={c.id} className="flex flex-wrap items-center gap-2">
                <Link href={`/changes/${c.id}`} className="font-mono font-semibold text-brand underline" dir="ltr">{c.number}</Link>
                <span dir="auto">{c.title}</span> <ChangeStageBadge stage={c.stage} /> <RiskBadge level={c.riskLevel} />
              </li>
            ))}
          </ul>
        )}
      </section>
      {mayPlan && <PlanMeetingForm csrfToken={session.data.csrfToken} />}
      <section aria-labelledby="meetings-heading" className="space-y-2">
        <h2 id="meetings-heading" className="text-xl font-semibold">{t("meetings")}</h2>
        {!meetings || meetings.length === 0 ? <p>{t("noMeetings")}</p> : (
          <div className="overflow-x-auto">
            <table className={tableClass} data-testid="meeting-table">
              <caption className="sr-only">{t("meetings")}</caption>
              <thead>
                <tr>
                  <th scope="col" className={cellClass}>{t("meeting")}</th>
                  <th scope="col" className={cellClass}>{t("when")}</th>
                  <th scope="col" className={cellClass}>{t("chair")}</th>
                  <th scope="col" className={cellClass}>{t("state")}</th>
                  <th scope="col" className={cellClass}>{t("agenda")}</th>
                </tr>
              </thead>
              <tbody>
                {meetings.map((m) => (
                  <tr key={m.id}>
                    <td className={cellClass}><Link href={`/changes/board/${m.id}`} className="font-semibold text-brand underline"><Localized text={m.title} locale={l} /></Link></td>
                    <td className={cellClass}>{formatDateTime(new Date(m.startsAt), display)}</td>
                    <td className={cellClass}>{m.chair.displayName ?? "—"}</td>
                    <td className={cellClass}>{t(`states.${m.state}`)}</td>
                    <td className={cellClass}>{t("decidedOf", { decided: m.decidedCount, count: m.itemCount })}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </section>
  );
}

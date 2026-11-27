import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { MeetingView } from "@/components/changes/ChangeBoard";
import { Localized } from "@/components/directory/common";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { Meeting } from "@/lib/changes";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import type { Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/changes/board/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "changes.board" });
  return { title: t("title") };
}

/** A meeting of the change advisory board (task H4): the agenda with votes and the chair's decisions, and the minutes. */
export default async function MeetingPage({ params }: PageProps<"/[locale]/changes/board/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/changes/board/${id}`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "changes.board" }), getTranslations({ locale, namespace: "changes.list" })]);
  if (!user?.permissions.includes("changes.read")) {
    return <p role="alert">{user ? tList("noAccess") : tList("unavailable")}</p>;
  }
  const [meeting, format] = await Promise.all([getJsonAs<Meeting>(`/api/v1/cab-meetings/${id}`, session.data.accessToken, tenant), getDisplayFormat()]);
  if (!meeting) notFound();
  return (
    <article aria-labelledby="meeting-heading" className="space-y-6">
      <Link href="/changes/board" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("title")}</Link>
      <h1 id="meeting-heading" className="text-3xl font-bold text-brand"><Localized text={meeting.title} locale={locale as Locale} /></h1>
      <MeetingView initial={meeting} csrfToken={session.data.csrfToken} display={{ locale, ...format }} />
    </article>
  );
}

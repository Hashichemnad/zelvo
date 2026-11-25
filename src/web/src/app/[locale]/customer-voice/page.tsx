import { getTranslations, setRequestLocale } from "next-intl/server";
import { Pagination } from "@/components/incidents/IncidentTable";
import { VoiceTable } from "@/components/voice/VoiceViews";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { ticketStates } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";
import { voiceTypes, type VoicePage } from "@/lib/voice";

export async function generateMetadata({ params }: PageProps<"/[locale]/customer-voice">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "voice.staff" });
  return { title: t("title") };
}

/** Complaints, inquiries and suggestions for staff (voice.read): filter by type, open, state, assignee and group. */
export default async function CustomerVoicePage({ params, searchParams }: PageProps<"/[locale]/customer-voice">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/customer-voice`);
  const t = await getTranslations({ locale, namespace: "voice.staff" });
  const tVoice = await getTranslations({ locale, namespace: "voice" });
  const tStates = await getTranslations({ locale, namespace: "incidents.states" });
  if (!user?.permissions.includes("voice.read")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const search = await searchParams;
  const pick = (key: string, allowed: readonly string[]) => (typeof search[key] === "string" && allowed.includes(search[key]) ? search[key] : "");
  const type = pick("type", voiceTypes);
  const state = pick("state", ticketStates);
  const assignee = pick("assignee", ["me", "none"]);
  const group = pick("group", ["mine", "none"]);
  const open = search.open === "false" ? "false" : "true";
  const requested = Number(search.page);
  const page = Number.isInteger(requested) && requested > 0 && requested < 10_000 ? requested : 1;
  const filter: Record<string, string> = { ...(type ? { type } : {}), ...(state ? { state } : { open }), ...(assignee ? { assignee } : {}), ...(group ? { group } : {}) };
  const [records, format] = await Promise.all([
    getJsonAs<VoicePage>(`/api/v1/customer-voice?${new URLSearchParams({ ...filter, page: String(page), pageSize: "25" })}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  const input = "rounded-md border border-line bg-surface px-3 py-2";
  return (
    <section aria-labelledby="voice-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="voice-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        {user.permissions.includes("voice.report") && <Link href="/customer-voice/report" className="font-semibold text-brand underline">{t("report")}</Link>}
      </div>
      <form method="get" className="flex flex-wrap items-end gap-3" aria-label={t("filters")}>
        <div className="space-y-1">
          <label htmlFor="voice-type" className="block text-sm">{tVoice("fields.type")}</label>
          <select id="voice-type" name="type" defaultValue={type} className={input}>
            <option value="">{t("anyType")}</option>
            {voiceTypes.map((v) => <option key={v} value={v}>{tVoice(`types.${v}`)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor="voice-state" className="block text-sm">{tVoice("fields.state")}</label>
          <select id="voice-state" name="state" defaultValue={state} className={input}>
            <option value="">{t("anyState")}</option>
            {ticketStates.map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor="voice-open" className="block text-sm">{t("openFilter")}</label>
          <select id="voice-open" name="open" defaultValue={open} className={input}>
            <option value="true">{t("openOnly")}</option>
            <option value="false">{t("all")}</option>
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor="voice-assignee" className="block text-sm">{tVoice("fields.assignee")}</label>
          <select id="voice-assignee" name="assignee" defaultValue={assignee} className={input}>
            <option value="">{t("anyone")}</option>
            <option value="me">{t("me")}</option>
            <option value="none">{tVoice("nobody")}</option>
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor="voice-group" className="block text-sm">{t("group")}</label>
          <select id="voice-group" name="group" defaultValue={group} className={input}>
            <option value="">{t("anyGroup")}</option>
            <option value="mine">{t("myGroups")}</option>
            <option value="none">{t("noGroup")}</option>
          </select>
        </div>
        <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("applyFilters")}</button>
      </form>
      {!records ? <p role="alert">{t("unavailable")}</p> : records.items.length === 0 ? <p>{t("empty")}</p> : (
        <>
          <VoiceTable records={records.items} basePath="/customer-voice" staff display={{ locale, ...format }} />
          <Pagination page={records.page} pageSize={records.pageSize} totalCount={records.totalCount} query={filter} basePath="/customer-voice" />
        </>
      )}
    </section>
  );
}

import { getTranslations, setRequestLocale } from "next-intl/server";
import { Pagination } from "@/components/incidents/IncidentTable";
import { VoiceTable } from "@/components/voice/VoiceViews";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";
import type { VoicePage } from "@/lib/voice";

export async function generateMetadata({ params }: PageProps<"/[locale]/my-voice">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "voice.mine" });
  return { title: t("title") };
}

/** The user's own complaints, inquiries and suggestions (task C6; the portal's "My tickets", D2, lists them too). */
export default async function MyVoicePage({ params, searchParams }: PageProps<"/[locale]/my-voice">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/my-voice`);
  const t = await getTranslations({ locale, namespace: "voice.mine" });
  const requested = Number((await searchParams).page);
  const page = Number.isInteger(requested) && requested > 0 && requested < 10_000 ? requested : 1;
  const [records, format] = await Promise.all([
    getJsonAs<VoicePage>(`/api/v1/customer-voice?${new URLSearchParams({ mine: "true", page: String(page), pageSize: "25" })}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="my-voice-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 id="my-voice-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        <Link href="/contact" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("new")}</Link>
      </div>
      {!records ? <p role="alert">{t("unavailable")}</p> : records.items.length === 0 ? <p>{t("empty")}</p> : (
        <>
          <VoiceTable records={records.items} basePath="/my-voice" staff={false} display={{ locale, ...format }} />
          <Pagination page={records.page} pageSize={records.pageSize} totalCount={records.totalCount} query={{}} basePath="/my-voice" />
        </>
      )}
    </section>
  );
}

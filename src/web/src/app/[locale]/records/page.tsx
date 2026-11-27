import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { RecordType } from "@/lib/records";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/records">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "records.index" });
  return { title: t("title") };
}

/** The custom record types a person may raise or work on (task K2), for requesters and staff alike. */
export default async function RecordsIndexPage({ params }: PageProps<"/[locale]/records">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/records`);
  const t = await getTranslations({ locale, namespace: "records.index" });
  const types = (await getJsonAs<RecordType[]>("/api/v1/record-types", session.data.accessToken, tenant)) ?? [];
  const l = locale as Locale;

  return (
    <section aria-labelledby="records-heading" className="space-y-6">
      <h1 id="records-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      {types.filter((type) => type.active).length === 0 ? <p>{t("none")}</p> : (
        <ul className="grid gap-4 sm:grid-cols-2" data-testid="record-type-cards">
          {types.filter((type) => type.active).map((type) => {
            const name = inLanguage(type.name, l);
            const description = inLanguage(type.description, l);
            return (
              <li key={type.id} className="space-y-2 rounded-lg border border-line p-4">
                <h2 className="text-xl font-semibold"><span lang={name.lang} dir={name.dir}>{name.value}</span></h2>
                {description.value && <p lang={description.lang} dir={description.dir}>{description.value}</p>}
                <div className="flex flex-wrap gap-3">
                  {type.access.raise && (
                    <Link href={`/records/${type.key}/new`} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("raise")}</Link>
                  )}
                  <Link href={`/records/${type.key}`} className="px-2 py-2 font-semibold text-brand underline-offset-4 hover:underline">
                    {type.access.read ? t("all") : t("mine")}
                  </Link>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

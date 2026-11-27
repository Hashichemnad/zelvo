import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { RecordForm } from "@/components/records/RecordForm";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { RecordType } from "@/lib/records";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/records/[type]/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "records.form" });
  return { title: t("heading") };
}

/** Raise a record of a custom type (task K2): from the portal when the type has a portal entry, or by staff. */
export default async function NewRecordPage({ params }: PageProps<"/[locale]/records/[type]/new">) {
  const { locale, type: key } = await params;
  setRequestLocale(locale);
  if (!/^[a-z][a-z0-9-]{0,39}$/.test(key)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/records/${key}/new`);
  const t = await getTranslations({ locale, namespace: "records.form" });
  const token = session.data.accessToken;
  const [type, choices] = await Promise.all([
    getJsonAs<RecordType>(`/api/v1/record-types/${key}`, token, tenant),
    loadDirectoryChoices(token, tenant, user?.permissions ?? []),
  ]);
  if (!type) notFound();
  const name = inLanguage(type.name, locale as Locale);

  return (
    <section aria-labelledby="new-record-heading" className="space-y-6">
      <Link href={`/records/${key}`} className="text-sm font-semibold text-brand underline-offset-4 hover:underline">
        <span lang={name.lang} dir={name.dir}>{name.value}</span>
      </Link>
      <h1 id="new-record-heading" className="text-3xl font-bold text-brand">{t("heading")}: <span lang={name.lang} dir={name.dir}>{name.value}</span></h1>
      {!type.access.raise || !type.active ? <p role="alert">{t("notAllowed")}</p> : (
        <RecordForm type={type} csrfToken={session.data.csrfToken} categories={choices.categories} locations={choices.locations} staff={type.access.edit} />
      )}
    </section>
  );
}

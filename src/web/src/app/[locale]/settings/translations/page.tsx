import { getFormatter, getTranslations, setRequestLocale } from "next-intl/server";
import { getJsonAs } from "@/lib/api";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/translations">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.translations" });
  return { title: t("title") };
}

interface MissingTranslation {
  entityType: string;
  entityId: string;
  field: string;
  missingLanguages: ("en" | "ar")[];
}

const pageSize = 100;

/** Settings → Missing translations: bilingual texts still missing a language (LANG-5). */
export default async function TranslationsPage({ params }: PageProps<"/[locale]/settings/translations">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/translations`);
  const [t, tSection, tSettings, tDisplay, format] = await Promise.all([
    getTranslations({ locale, namespace: "settings.translations" }),
    getTranslations({ locale, namespace: "settings.sections.translations" }),
    getTranslations({ locale, namespace: "settings" }),
    getTranslations({ locale, namespace: "display" }),
    getFormatter({ locale }),
  ]);
  const page = user?.permissions.includes("translations.read")
    ? await getJsonAs<{ items: MissingTranslation[]; totalCount: number }>(
        `/api/v1/translations/missing?pageSize=${pageSize}`,
        session.data.accessToken,
        tenant,
      )
    : null;
  // Known names are translated; anything a later module adds before its translations shows its key.
  const label = (group: "entityTypes" | "fields", key: string) => (t.has(`${group}.${key}`) ? t(`${group}.${key}`) : key);

  return (
    <section aria-labelledby="translations-heading" className="space-y-4">
      <h2 id="translations-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!page ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : page.totalCount === 0 ? (
        <p data-testid="translations-complete">{t("none")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <p className="text-sm text-ink-muted">{t("total", { count: page.totalCount })}</p>
          <table className="w-full border-collapse text-start text-sm" data-testid="missing-translations">
            <thead>
              <tr className="border-b border-line">
                <th scope="col" className="py-2 text-start">{t("entity")}</th>
                <th scope="col" className="py-2 text-start">{t("field")}</th>
                <th scope="col" className="py-2 text-start">{t("missing")}</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((item) => (
                <tr key={`${item.entityType}:${item.entityId}:${item.field}`} className="border-b border-line">
                  <td className="py-2">{label("entityTypes", item.entityType)}</td>
                  <td className="py-2">{label("fields", item.field)}</td>
                  <td className="py-2">{format.list(item.missingLanguages.map((l) => tDisplay(`languages.${l}`)))}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}

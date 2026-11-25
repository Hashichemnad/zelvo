import { getTranslations, setRequestLocale } from "next-intl/server";
import { MarkdownView } from "@/components/knowledge/MarkdownView";
import { getDirection, otherLocale, type Locale } from "@/i18n/routing";
import { getPublishedPrivacyNotice } from "@/lib/api";
import { getRequestAuthConfig } from "@/lib/auth/config";
import { getDisplayFormat } from "@/lib/display";
import { formatLongDate } from "@/lib/format";

export async function generateMetadata({ params }: PageProps<"/[locale]/privacy">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "privacy.notice" });
  return { title: t("title") };
}

/** The organisation's published privacy notice (GOV-5, task F5), open to visitors. */
export default async function PrivacyNoticePage({ params }: PageProps<"/[locale]/privacy">) {
  const { locale: raw } = await params;
  const locale = raw as Locale;
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "privacy.notice" });
  const notice = await getPublishedPrivacyNotice((await getRequestAuthConfig()).tenant);
  if (!notice) {
    return (
      <section className="space-y-4">
        <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
        <p role="status">{t("none")}</p>
      </section>
    );
  }
  const other = otherLocale(locale);
  const own = notice.body[locale];
  const body = own ?? notice.body[other] ?? "";
  const title = notice.title[locale] ?? notice.title[other] ?? t("title");
  const format = await getDisplayFormat();
  return (
    <article aria-labelledby="privacy-notice-heading" className="space-y-4" data-testid="privacy-notice">
      <h1 id="privacy-notice-heading" className="text-3xl font-bold text-brand">{title}</h1>
      <p className="text-sm text-ink-muted">
        {t("version", { version: notice.version, date: notice.publishedAt ? formatLongDate(new Date(notice.publishedAt), { locale, ...format }) : "" })}
      </p>
      {!own && <p className="text-sm">{t("otherLanguage")}</p>}
      <MarkdownView markdown={body} articleId={null} lang={own ? undefined : other} dir={own ? undefined : getDirection(other)} />
    </article>
  );
}

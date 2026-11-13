import { getTranslations, setRequestLocale } from "next-intl/server";
import type { Locale } from "@/i18n/routing";

const reasons = ["expired", "cancelled", "failed", "refused", "mfa"] as const;
type Reason = (typeof reasons)[number] | "unavailable";

export async function generateMetadata({ params }: PageProps<"/[locale]/sign-in-problem">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "signInProblem" });
  return { title: t("title") };
}

/** Shown when sign-in could not finish; explains why and offers to try again. */
export default async function SignInProblemPage({ params, searchParams }: PageProps<"/[locale]/sign-in-problem">) {
  const { locale: rawLocale } = await params;
  const locale = rawLocale as Locale;
  setRequestLocale(locale);
  const query = await searchParams;
  const raw = typeof query.reason === "string" ? query.reason : "";
  const reason: Reason = (reasons as readonly string[]).includes(raw) ? (raw as Reason) : "unavailable";
  const t = await getTranslations({ locale, namespace: "signInProblem" });
  const retry = new URLSearchParams({ locale, returnTo: `/${locale}/account` });

  return (
    <section className="space-y-4">
      <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p role="alert">{t(`reasons.${reason}`)}</p>
      <a
        href={`/auth/login?${retry}`}
        className="inline-block rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand"
      >
        {t("tryAgain")}
      </a>
    </section>
  );
}

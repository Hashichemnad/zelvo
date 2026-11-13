import { getTranslations } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getAuthConfig } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";

/** Warns administrators while bilingual texts still miss a language (LANG-5). */
export async function MissingTranslationsWarning() {
  const config = getAuthConfig();
  const session = await getSession(config);
  if (!session) {
    return null;
  }
  const page = await getJsonAs<{ totalCount: number }>("/api/v1/translations/missing?pageSize=1", session.data.accessToken, config.tenant);
  if (!page || page.totalCount === 0) {
    return null;
  }
  const t = await getTranslations("settings");
  return (
    <p className="rounded-md border-s-4 border-warn bg-surface-muted px-4 py-3 text-sm" data-testid="missing-translations-warning">
      {t("missingWarning", { count: page.totalCount })}{" "}
      <Link href="/settings/translations" className="font-semibold text-brand underline underline-offset-4">
        {t("missingWarningLink")}
      </Link>
    </p>
  );
}

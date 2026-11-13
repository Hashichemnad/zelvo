import { getTranslations } from "next-intl/server";
import { getSession } from "@/lib/auth/session";
import { AccountMenu } from "./AccountMenu";
import { LanguageSwitcher } from "./LanguageSwitcher";

export async function SiteHeader() {
  const t = await getTranslations("common");
  const session = await getSession().catch(() => null);

  return (
    <header className="border-b border-line bg-surface">
      <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-4 px-4 py-3">
        <span className="text-lg font-bold text-brand">{t("productName")}</span>
        <div className="flex flex-wrap items-center gap-3">
          <AccountMenu
            userName={session ? (session.data.name ?? session.data.email ?? session.data.subject) : null}
            csrfToken={session?.data.csrfToken ?? null}
          />
          <LanguageSwitcher />
        </div>
      </div>
    </header>
  );
}

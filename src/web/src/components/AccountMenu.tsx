import { useLocale, useTranslations } from "next-intl";

export interface AccountMenuProps {
  /** The signed-in user's name, or null when nobody is signed in. */
  userName: string | null;
  /** The session's CSRF token, sent with the sign-out form. */
  csrfToken: string | null;
}

const buttonClass =
  "rounded-md border border-line px-3 py-1.5 text-sm font-semibold text-ink hover:bg-surface-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

/**
 * Sign in, or the signed-in user's name with a link to the account page and a sign-out button.
 * Sign-out is a POST form with the CSRF token, never a link.
 */
export function AccountMenu({ userName, csrfToken }: AccountMenuProps) {
  const t = useTranslations("auth");
  const locale = useLocale();

  if (!userName || !csrfToken) {
    const params = new URLSearchParams({ locale, returnTo: `/${locale}/account` });
    return (
      <a href={`/auth/login?${params}`} className={buttonClass}>
        {t("signIn")}
      </a>
    );
  }

  return (
    <div className="flex items-center gap-2">
      <a href={`/${locale}/account`} className="text-sm font-semibold text-brand underline-offset-4 hover:underline">
        {/* Accessible name "Account of <name>" contains the visible name (WCAG 2.5.3). */}
        <span className="sr-only">{t("accountOf")}</span> {userName}
      </a>
      <form action="/auth/logout" method="post">
        <input type="hidden" name="csrf" value={csrfToken} />
        <input type="hidden" name="locale" value={locale} />
        <button type="submit" className={buttonClass}>
          {t("signOut")}
        </button>
      </form>
    </div>
  );
}

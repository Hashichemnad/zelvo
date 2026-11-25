import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";

/**
 * The "Powered by Zelvo" mark (module 11). Always shown; it is deliberately not driven by tenant
 * branding settings and has no option to hide it.
 */
/** Task F5: `privacyNotice` adds a link to the organisation's published privacy notice. */
export function PoweredByFooter({ privacyNotice = false }: { privacyNotice?: boolean }) {
  const t = useTranslations("common");

  return (
    <footer className="border-t border-line bg-surface">
      {privacyNotice && (
        <p className="mx-auto max-w-5xl px-4 pt-4 text-center text-sm">
          <Link href="/privacy" className="text-brand underline" data-testid="privacy-notice-link">{t("privacyNotice")}</Link>
        </p>
      )}
      <p className="mx-auto max-w-5xl px-4 py-4 text-center text-sm text-ink-muted" data-testid="powered-by">
        {t("poweredBy")}
      </p>
    </footer>
  );
}

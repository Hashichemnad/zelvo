import { useTranslations } from "next-intl";

/**
 * The "Powered by Zelvo" mark (module 11). Always shown; it is deliberately not driven by tenant
 * branding settings and has no option to hide it.
 */
export function PoweredByFooter() {
  const t = useTranslations("common");

  return (
    <footer className="border-t border-line bg-surface">
      <p className="mx-auto max-w-5xl px-4 py-4 text-center text-sm text-ink-muted" data-testid="powered-by">
        {t("poweredBy")}
      </p>
    </footer>
  );
}

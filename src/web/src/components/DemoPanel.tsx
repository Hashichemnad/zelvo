import { useLocale, useTranslations } from "next-intl";
import type { DemoInfo, LocalizedText } from "@/lib/api";
import type { DigitSystem } from "@/lib/format";
import { DemoResetButton } from "./DemoResetButton";

export interface DemoPanelProps {
  demo: DemoInfo;
  /** Username of the signed-in demo user, or null for visitors. */
  currentUsername: string | null;
  /** The session's CSRF token (signed in), sent with the switch forms. */
  csrfToken: string | null;
  /** Whether the signed-in user may reset the demo (demo.reset). */
  canReset: boolean;
  /** When the demo was last reset, already formatted in the user's calendar and digits. */
  lastReset: string | null;
  /** The person's digits, for the reset's progress. */
  digits?: DigitSystem;
}

const buttonClass =
  "w-full rounded-md border border-line bg-surface px-3 py-2 text-start text-sm text-ink hover:bg-surface-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";

function pick(text: LocalizedText, locale: string): string {
  return (locale === "ar" ? (text.ar ?? text.en) : (text.en ?? text.ar)) ?? "";
}

/**
 * Demo installations only (task D): the demo panel under the header. One click signs in as the
 * ready-made demo user of a persona, or opens the other sample organisation; administrators can
 * reset the demo. Plain forms (POST /auth/demo), so it works without JavaScript and by keyboard.
 */
export function DemoPanel({ demo, currentUsername, csrfToken, canReset, lastReset, digits = "latn" }: DemoPanelProps) {
  const t = useTranslations("demo");
  const locale = useLocale();
  const organisation = demo.organisations.find((o) => o.slug === demo.organisation);
  const others = demo.organisations.filter((o) => o.slug !== demo.organisation);

  const hidden = (tenant: string) => (
    <>
      <input type="hidden" name="tenant" value={tenant} />
      <input type="hidden" name="locale" value={locale} />
      {csrfToken && <input type="hidden" name="csrf" value={csrfToken} />}
    </>
  );

  return (
    <section aria-labelledby="demo-panel-heading" className="border-b border-line bg-surface-muted">
      <details className="mx-auto max-w-5xl px-4 py-2">
        <summary className="cursor-pointer text-sm font-semibold text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
          <span id="demo-panel-heading">{t("title")}</span>
          {organisation && <span className="font-normal text-ink-muted"> · {pick(organisation.name, locale)}</span>}
        </summary>
        <div className="grid gap-4 py-3 md:grid-cols-3">
          <div className="md:col-span-2">
            <h2 className="mb-2 text-sm font-semibold text-ink">{t("switchUser")}</h2>
            <ul className="grid gap-2 sm:grid-cols-2">
              {demo.users.map((user) => {
                const current = user.username === currentUsername;
                return (
                  <li key={user.persona}>
                    <form action="/auth/demo" method="post">
                      {hidden(demo.organisation)}
                      <input type="hidden" name="persona" value={user.persona} />
                      <button type="submit" className={buttonClass} aria-current={current ? "true" : undefined} disabled={current}>
                        <span className="block font-semibold">{pick(user.roleName, locale)}</span>
                        <span className="block text-ink-muted" dir="auto">
                          {user.displayName}
                          {current && ` · ${t("current")}`}
                        </span>
                      </button>
                    </form>
                  </li>
                );
              })}
            </ul>
          </div>
          <div className="flex flex-col gap-3">
            {others.length > 0 && (
              <div>
                <h2 className="mb-2 text-sm font-semibold text-ink">{t("switchOrganisation")}</h2>
                <ul className="grid gap-2">
                  {others.map((other) => (
                    <li key={other.slug}>
                      <form action="/auth/demo" method="post">
                        {hidden(other.slug)}
                        <button type="submit" className={buttonClass}>
                          <span className="block font-semibold">{pick(other.name, locale)}</span>
                          <span className="block text-ink-muted">{t(other.kind === "Government" ? "kindGovernment" : "kindCompany")}</span>
                        </button>
                      </form>
                    </li>
                  ))}
                </ul>
              </div>
            )}
            {lastReset && <p className="text-sm text-ink-muted">{t("lastReset", { date: lastReset })}</p>}
            {canReset && csrfToken && (
              <DemoResetButton
                csrfToken={csrfToken}
                digits={digits}
                organisationNames={Object.fromEntries(demo.organisations.map((o) => [o.slug, pick(o.name, locale)]))}
              />
            )}
          </div>
        </div>
      </details>
    </section>
  );
}

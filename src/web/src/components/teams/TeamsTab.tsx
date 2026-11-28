"use client";

import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { primaryButton } from "../settings/integrations/ui";

type State = "starting" | "outsideTeams" | "ready" | "signingIn" | "failed";

/**
 * Task J4: the Zelvo tab in Teams before the person has signed in there. Teams cannot show
 * Zelvo's sign-in page inside its frame, so "Sign in" opens it in a pop-up
 * (`authentication.authenticate`); the pop-up hands back a one-time code, which the tab exchanges
 * for its own session cookie, then the tab opens the chosen page.
 */
export function TeamsTab({ target }: { target: string }) {
  const t = useTranslations("teams.tab");
  const locale = useLocale();
  const [state, setState] = useState<State>("starting");

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const { app } = await import("@microsoft/teams-js");
        await app.initialize();
        if (!cancelled) setState("ready");
      } catch {
        if (!cancelled) setState("outsideTeams");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  async function signIn() {
    setState("signingIn");
    try {
      const { authentication } = await import("@microsoft/teams-js");
      const code = await authentication.authenticate({ url: `${window.location.origin}/auth/teams/start?locale=${locale}`, width: 600, height: 700 });
      const response = await fetch("/auth/teams/session", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code }),
      });
      if (!response.ok) throw new Error(`session ${response.status}`);
      window.location.assign(target);
    } catch {
      setState("failed");
    }
  }

  return (
    <section aria-labelledby="teams-tab-heading" className="mx-auto max-w-lg space-y-4 py-8 text-center" data-testid="teams-tab">
      <h1 id="teams-tab-heading" className="text-2xl font-bold text-brand">{t("title")}</h1>
      <p>{t("intro")}</p>
      {state === "outsideTeams" ? (
        <p role="note">{t("outsideTeams")}</p>
      ) : (
        <button type="button" className={primaryButton} onClick={signIn} disabled={state === "starting" || state === "signingIn"}>
          {t("signIn")}
        </button>
      )}
      <p aria-live="polite" className="text-bad">{state === "failed" ? t("failed") : ""}</p>
    </section>
  );
}

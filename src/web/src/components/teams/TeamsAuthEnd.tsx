"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";

/**
 * Task J4: the last page of the Teams sign-in pop-up. Signed in here (first party), it asks for a
 * one-time code and gives it to the tab through Teams (`authentication.notifySuccess`), which
 * closes the pop-up.
 */
export function TeamsAuthEnd({ csrfToken }: { csrfToken: string | null }) {
  const t = useTranslations("teams.tab");
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    void (async () => {
      const { app, authentication } = await import("@microsoft/teams-js");
      try {
        await app.initialize();
        if (!csrfToken) throw new Error("not signed in");
        const response = await fetch("/auth/teams/handoff", { method: "POST", headers: { "X-CSRF-Token": csrfToken } });
        if (!response.ok) throw new Error(`handoff ${response.status}`);
        const { code } = (await response.json()) as { code: string };
        authentication.notifySuccess(code);
      } catch (error) {
        setFailed(true);
        try {
          authentication.notifyFailure(error instanceof Error ? error.message : "failed");
        } catch {
          // Not inside Teams: nothing to tell.
        }
      }
    })();
  }, [csrfToken]);

  return (
    <p role="status" className="py-8 text-center">
      {failed ? t("failed") : t("finishing")}
    </p>
  );
}

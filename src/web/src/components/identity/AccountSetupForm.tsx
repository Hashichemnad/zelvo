"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, postJson } from "@/lib/client-api";

const input = "block w-full rounded-md border border-line bg-surface px-3 py-2";

/** The token from the address's fragment (`#<token>`), which never reaches a server. */
export function tokenFromHash(hash: string): string | null {
  const token = hash.replace(/^#/, "").trim();
  return /^[A-Za-z0-9_-]{20,100}$/.test(token) ? token : null;
}

/**
 * Task M2: choose a password with a one-time setup link. The link is checked first (who it is
 * for); Keycloak's password policy decides on the password. After it, the person signs in and
 * sets up the authenticator app (Keycloak asks for it at the first sign-in).
 */
export function AccountSetupForm() {
  const t = useTranslations("accountSetup");
  const locale = useLocale();
  const id = useId();
  const [token, setToken] = useState<string | null>(null);
  const [state, setState] = useState<"checking" | "invalid" | "ready" | "saving" | "done">("checking");
  const [email, setEmail] = useState<string | null>(null);
  const [password, setPassword] = useState("");
  const [repeat, setRepeat] = useState("");
  const [problem, setProblem] = useState<string | null>(null);

  useEffect(() => {
    const found = tokenFromHash(window.location.hash);
    // Take the token out of the address bar and the history.
    window.history.replaceState(null, "", window.location.pathname);
    if (!found) {
      queueMicrotask(() => setState("invalid"));
      return;
    }
    postJson<{ email: string | null }>("/v1/identity/account-setup/check", { token: found }, "")
      .then((answer) => {
        setToken(found);
        setEmail(answer.email);
        setState("ready");
      })
      .catch(() => setState("invalid"));
  }, []);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setProblem(null);
    if (password !== repeat) {
      setProblem(t("mismatch"));
      return;
    }
    setState("saving");
    try {
      await fetch("/bff/api/v1/identity/account-setup", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ token, password }),
      }).then(async (response) => {
        if (!response.ok) {
          const body = (await response.json().catch(() => ({}))) as { code?: string; policy?: string };
          throw new ApiError("account setup", response.status, body.code ?? null, body);
        }
      });
      setPassword("");
      setRepeat("");
      setState("done");
    } catch (error) {
      setState("ready");
      if (error instanceof ApiError && error.code === "password-refused") {
        const policy = typeof error.details.policy === "string" ? error.details.policy : "";
        setProblem(t.has(`policies.${policy}`) ? t(`policies.${policy}`) : t("refused"));
      } else if (error instanceof ApiError && error.code === "setup-link-invalid") {
        setState("invalid");
      } else {
        setProblem(error instanceof ApiError && error.rateLimited ? t("rateLimited") : t("failed"));
      }
    }
  }

  const signIn = `/auth/login?${new URLSearchParams({ locale, returnTo: `/${locale}/settings/license` })}`;
  return (
    <section aria-labelledby={`${id}-title`} className="max-w-xl space-y-4" data-testid="account-setup">
      <h1 id={`${id}-title`} className="text-3xl font-bold text-brand">{t("title")}</h1>
      {state === "checking" && <p role="status">{t("checking")}</p>}
      {state === "invalid" && <p role="alert">{t("invalid")}</p>}
      {state === "done" && (
        <div className="space-y-3">
          <p role="status">{t("done")}</p>
          <a href={signIn} className="inline-block rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("signIn")}</a>
        </div>
      )}
      {(state === "ready" || state === "saving") && (
        <form onSubmit={submit} className="space-y-4" noValidate>
          <p>{t.rich("intro", { email: email ?? "", address: (chunks) => <bdi dir="ltr" className="font-semibold">{chunks}</bdi> })}</p>
          <div className="space-y-1">
            <label htmlFor={`${id}-password`} className="block font-semibold">{t("password")}</label>
            <input id={`${id}-password`} type="password" autoComplete="new-password" value={password} maxLength={128} required
              onChange={(e) => setPassword(e.target.value)} className={input} aria-describedby={`${id}-hint`} />
            <p id={`${id}-hint`} className="text-sm text-ink-muted">{t("hint")}</p>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-repeat`} className="block font-semibold">{t("repeat")}</label>
            <input id={`${id}-repeat`} type="password" autoComplete="new-password" value={repeat} maxLength={128} required
              onChange={(e) => setRepeat(e.target.value)} className={input} />
          </div>
          <button type="submit" disabled={state === "saving" || !password} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface disabled:opacity-60">
            {state === "saving" ? t("saving") : t("save")}
          </button>
          <p role="alert" className="text-bad">{problem ?? ""}</p>
        </form>
      )}
    </section>
  );
}

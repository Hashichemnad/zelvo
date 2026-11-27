"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { deleteRequest, getJson } from "@/lib/client-api";
import { addPasskeyHref, type MyPasskeys } from "@/lib/passkeys";
import { primaryButton, secondaryButton } from "../settings/integrations/ui";

/**
 * Account → Security → Passkeys (task J3): sign in with the phone's or computer's fingerprint,
 * face or PIN. Adding one goes through Keycloak's own passkey step and comes back here; removing
 * one calls the API. Staff see that a passkey sign-in is their second factor.
 */
export function PasskeysPanel({ initial, csrfToken, outcome }: { initial: MyPasskeys; csrfToken: string; outcome: string | null }) {
  const t = useTranslations("account.security");
  const locale = useLocale();
  const format = useFormatter();
  const [value, setValue] = useState(initial);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(
    outcome === "added" ? { ok: true, text: t("added") } : outcome === "cancelled" ? { ok: false, text: t("cancelled") } : outcome === "error" ? { ok: false, text: t("addFailed") } : null,
  );

  if (value.mode === "Off") {
    return <p role="note" className="max-w-2xl text-ink-muted" data-testid="passkeys-off">{t("off")}</p>;
  }

  async function remove(id: string) {
    setMessage(null);
    try {
      await deleteRequest(`/v1/me/passkeys/${encodeURIComponent(id)}`, csrfToken);
      setValue(await getJson<MyPasskeys>("/v1/me/passkeys"));
      setMessage({ ok: true, text: t("removed") });
    } catch {
      setMessage({ ok: false, text: t("failed") });
    }
  }

  return (
    <div className="max-w-2xl space-y-4" data-testid="passkeys">
      <p>{t("intro")}</p>
      {value.secondFactor && (
        <p role="note" className="rounded-md border border-line bg-surface-muted p-3 text-sm">
          {value.mode === "RequiredForStaff" ? t("requiredForStaff") : t("secondFactor")}
        </p>
      )}
      {value.passkeys.length === 0 ? (
        <p className="text-ink-muted">{t("none")}</p>
      ) : (
        <ul className="space-y-2">
          {value.passkeys.map((passkey) => {
            const name = passkey.label || t("unnamed");
            return (
              <li key={passkey.id} className="flex flex-wrap items-center justify-between gap-2 rounded border border-line p-3">
                <span>
                  <span className="block font-semibold" dir="auto">{name}</span>
                  {passkey.createdAt && (
                    <span className="block text-sm text-ink-muted">{t("createdAt", { date: format.dateTime(new Date(passkey.createdAt), { dateStyle: "medium" }) })}</span>
                  )}
                </span>
                <button type="button" className={secondaryButton} onClick={() => remove(passkey.id)} aria-label={t("removeNamed", { name })}>
                  {t("remove")}
                </button>
              </li>
            );
          })}
        </ul>
      )}
      <a href={addPasskeyHref(locale, `/${locale}/account/security`, value.secondFactor)} className={`${primaryButton} inline-block`} data-testid="add-passkey">
        {t("add")}
      </a>
      <p className="text-sm text-ink-muted">{t("addHelp")}</p>
      <p aria-live="polite" className={message?.ok ? "text-ok" : "text-bad"}>
        {message?.text ?? ""}
      </p>
    </div>
  );
}

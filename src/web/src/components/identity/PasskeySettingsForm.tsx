"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { putJson } from "@/lib/client-api";
import type { PasskeyMode } from "@/lib/passkeys";
import { primaryButton } from "../settings/integrations/ui";

const modes: PasskeyMode[] = ["Off", "Allowed", "RequiredForStaff"];

/** Settings → Sign-in → Passkeys (task J3): off, allowed (default) or required as staff's second factor. */
export function PasskeySettingsForm({ initial, csrfToken }: { initial: PasskeyMode; csrfToken: string }) {
  const t = useTranslations("settings.signIn.passkeys");
  const [mode, setMode] = useState(initial);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    try {
      const saved = await putJson<{ passkeys: PasskeyMode }>("/v1/identity/settings/passkeys", { mode }, csrfToken);
      setMode(saved.passkeys);
      setMessage({ ok: true, text: t("saved") });
    } catch {
      setMessage({ ok: false, text: t("failed") });
    }
  }

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-4" data-testid="passkey-settings">
      <fieldset className="space-y-3">
        <legend className="font-semibold">{t("legend")}</legend>
        {modes.map((m) => (
          <label key={m} className="flex items-start gap-2">
            <input type="radio" name="passkeys" className="mt-1" value={m} checked={mode === m} onChange={() => setMode(m)} aria-describedby={`passkeys-${m}-help`} />
            <span>
              <span className="block font-semibold">{t(`modes.${m}`)}</span>
              <span id={`passkeys-${m}-help`} className="block text-sm text-ink-muted">{t(`help.${m}`)}</span>
            </span>
          </label>
        ))}
      </fieldset>
      <p className="text-sm text-ink-muted">{t("addresses")}</p>
      <button type="submit" className={primaryButton}>{t("save")}</button>
      <p aria-live="polite" className={message?.ok ? "text-ok" : "text-bad"}>{message?.text ?? ""}</p>
    </form>
  );
}

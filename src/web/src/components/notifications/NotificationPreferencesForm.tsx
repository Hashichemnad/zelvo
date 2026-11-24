"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { putJson } from "@/lib/client-api";
import { eventKey, type NotificationAudience, type NotificationPreference } from "@/lib/notifications";
import { primaryButton } from "../settings/integrations/ui";

const audiences: NotificationAudience[] = ["Requester", "Staff", "Publisher"];

/**
 * Account → Notifications (product owner decision 2026-11-24): in-app notifications are always on;
 * each person chooses which also come by email. SLA alerts always come by email.
 */
export function NotificationPreferencesForm({ initial, csrfToken }: { initial: NotificationPreference[]; csrfToken: string }) {
  const t = useTranslations("notifications");
  const id = useId();
  const [preferences, setPreferences] = useState(initial);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    try {
      const saved = await putJson<NotificationPreference[]>(
        "/v1/me/notification-preferences",
        { events: preferences.filter((p) => p.canChange).map((p) => ({ eventType: p.eventType, email: p.email })) },
        csrfToken,
      );
      setPreferences(saved);
      setMessage({ ok: true, text: t("preferences.saved") });
    } catch {
      setMessage({ ok: false, text: t("preferences.failed") });
    }
  }

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-4" noValidate data-testid="notification-preferences">
      {audiences
        .filter((audience) => preferences.some((p) => p.audience === audience))
        .map((audience) => (
          <fieldset key={audience} className="space-y-2 rounded-lg border border-line p-3">
            <legend className="px-1 font-semibold">{t(`audiences.${audience}`)}</legend>
            {preferences
              .filter((p) => p.audience === audience)
              .map((p) => {
                const hintId = `${id}-${eventKey(p.eventType)}-hint`;
                const hint = !p.enabled
                  ? t("preferences.offByOrganisation")
                  : !p.emailEnabled
                    ? t("preferences.appOnlyByOrganisation")
                    : !p.canChange
                      ? t("preferences.alwaysEmail")
                      : null;
                return (
                  <div key={p.eventType}>
                    <label className="flex items-start gap-2">
                      <input
                        type="checkbox"
                        className="mt-1"
                        checked={p.email}
                        disabled={!p.canChange || !p.enabled || !p.emailEnabled}
                        aria-describedby={hint ? hintId : undefined}
                        onChange={(e) => setPreferences(preferences.map((q) => (q.eventType === p.eventType ? { ...q, email: e.target.checked } : q)))}
                      />
                      <span>
                        <span className="block">{t(`events.${eventKey(p.eventType)}`)}</span>
                        <span className="block text-sm text-ink-muted">{t("preferences.email")}</span>
                      </span>
                    </label>
                    {hint && (
                      <p id={hintId} className="ms-6 text-sm text-ink-muted">
                        {hint}
                      </p>
                    )}
                  </div>
                );
              })}
          </fieldset>
        ))}
      {message && (
        <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-bad"}>
          {message.text}
        </p>
      )}
      <button type="submit" className={primaryButton}>
        {t("preferences.save")}
      </button>
    </form>
  );
}

"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError, putJson } from "@/lib/client-api";
import type { MyNotificationChannels } from "@/lib/messaging";

/**
 * Account → Notifications (task E3): whether notifications also go by WhatsApp and SMS to the
 * phone of the person's profile, and to Microsoft Teams. Only the channels the organisation offers
 * are shown; WhatsApp and SMS need a phone number on the profile.
 */
export function NotificationChannelsForm({ initial, csrfToken }: { initial: MyNotificationChannels; csrfToken: string }) {
  const t = useTranslations("account.notificationChannels");
  const [value, setValue] = useState(initial);
  const [status, setStatus] = useState<"saved" | "noPhone" | "error" | null>(null);
  if (!value.whatsAppOffered && !value.smsOffered && !value.teamsOffered) return null;

  async function change(key: "whatsApp" | "sms" | "teams", on: boolean) {
    setStatus(null);
    try {
      setValue(await putJson<MyNotificationChannels>("/v1/me/notification-channels", { [key]: on }, csrfToken));
      setStatus("saved");
    } catch (e) {
      setStatus(e instanceof ApiError && e.status === 400 ? "noPhone" : "error");
    }
  }

  const option = (key: "whatsApp" | "sms" | "teams", offered: boolean, hint: string) =>
    offered && (
      <label className="flex items-start gap-2">
        <input type="checkbox" className="mt-1" checked={value[key]} onChange={(e) => change(key, e.target.checked)} aria-describedby={`channel-${key}-hint`} />
        <span>
          <span className="block font-semibold">{t(key)}</span>
          <span id={`channel-${key}-hint`} className="block text-sm text-ink-muted">{hint}</span>
        </span>
      </label>
    );

  return (
    <fieldset className="max-w-2xl space-y-3 rounded-md border border-line p-4" data-testid="notification-channels">
      <legend className="font-semibold">{t("heading")}</legend>
      {option("whatsApp", value.whatsAppOffered, value.phone ? t("toPhone", { phone: "⁦" + value.phone + "⁩" }) : t("needsPhone"))}
      {option("sms", value.smsOffered, value.phone ? t("toPhone", { phone: "⁦" + value.phone + "⁩" }) : t("needsPhone"))}
      {option("teams", value.teamsOffered, value.teamsLinked ? t("teamsLinked") : t("teamsNotLinked"))}
      <p aria-live="polite" className={status === "saved" ? "text-ok" : "text-bad"}>
        {status === "saved" ? t("saved") : status === "noPhone" ? t("needsPhone") : status === "error" ? t("error") : ""}
      </p>
    </fieldset>
  );
}

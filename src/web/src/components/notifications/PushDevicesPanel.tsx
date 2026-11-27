"use client";

import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { ApiError, deleteRequest, getJson, postJson } from "@/lib/client-api";
import { currentSubscription, needsHomeScreen, pushSupported, subscribeThisDevice, type PushStatus } from "@/lib/pwa/push";
import { primaryButton, secondaryButton } from "../settings/integrations/ui";

type Message = { ok: boolean; text: string } | null;

/**
 * Account → Notifications → This device and your devices (module 19, task J2): turn web push on
 * for this browser or installed app, see every device that receives notifications, remove one, and
 * send a test notification. Which events come by push is chosen per event in the list above.
 */
export function PushDevicesPanel({ initial, csrfToken }: { initial: PushStatus; csrfToken: string }) {
  const t = useTranslations("account.push");
  const format = useFormatter();
  const [status, setStatus] = useState(initial);
  const [thisEndpointKnown, setThisEndpointKnown] = useState<boolean | null>(null);
  const [support, setSupport] = useState<"unknown" | "yes" | "no" | "homeScreen">("unknown");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message>(null);

  useEffect(() => {
    // Browser features are known only in the browser.
    const supported = pushSupported() ? "yes" : needsHomeScreen() ? "homeScreen" : "no";
    void currentSubscription().then((s) => {
      setSupport(supported);
      setThisEndpointKnown(s !== null);
    });
  }, []);

  if (!status.available) {
    return (
      <p role="note" className="max-w-2xl rounded-md border border-line bg-surface-muted p-3 text-sm" data-testid="push-not-available">
        {t("notAvailable")}
      </p>
    );
  }

  async function reload() {
    setStatus(await getJson<PushStatus>("/v1/me/push"));
  }

  async function turnOn() {
    if (!status.publicKey) return;
    setBusy(true);
    setMessage(null);
    try {
      const result = await subscribeThisDevice(status.publicKey, csrfToken);
      if (result === "denied") {
        setMessage({ ok: false, text: t("denied") });
      } else {
        setThisEndpointKnown(true);
        await reload();
        setMessage({ ok: true, text: t("turnedOn") });
      }
    } catch {
      setMessage({ ok: false, text: t("failed") });
    } finally {
      setBusy(false);
    }
  }

  async function remove(id: string) {
    setMessage(null);
    try {
      await deleteRequest(`/v1/me/push/subscriptions/${id}`, csrfToken);
      await reload();
      setMessage({ ok: true, text: t("removed") });
    } catch {
      setMessage({ ok: false, text: t("failed") });
    }
  }

  async function test() {
    setMessage(null);
    try {
      await postJson("/v1/me/push/test", {}, csrfToken);
      setMessage({ ok: true, text: t("testSent") });
    } catch (error) {
      setMessage({ ok: false, text: error instanceof ApiError && error.code === "no-push-devices" ? t("noDevices") : t("failed") });
    }
  }

  return (
    <section className="max-w-2xl space-y-3 rounded-md border border-line p-4" aria-labelledby="push-heading" data-testid="push-devices">
      <h3 id="push-heading" className="font-semibold">{t("heading")}</h3>
      <p className="text-sm text-ink-muted">{t("intro")}</p>
      {support === "homeScreen" && <p role="note" className="text-sm">{t("homeScreen")}</p>}
      {support === "no" && <p role="note" className="text-sm">{t("unsupported")}</p>}
      {support === "yes" && !thisEndpointKnown && (
        <button type="button" className={primaryButton} onClick={turnOn} disabled={busy} data-testid="push-turn-on">
          {t("turnOn")}
        </button>
      )}
      {support === "yes" && thisEndpointKnown && <p className="text-sm text-ok">{t("thisDeviceOn")}</p>}

      <h4 className="font-semibold">{t("devices")}</h4>
      {status.devices.length === 0 ? (
        <p className="text-sm text-ink-muted">{t("noDevices")}</p>
      ) : (
        <ul className="space-y-2">
          {status.devices.map((device) => (
            <li key={device.id} className="flex flex-wrap items-center justify-between gap-2 rounded border border-line p-2">
              <span>
                <span className="block font-semibold" dir="auto">{device.deviceName}</span>
                <span className="block text-sm text-ink-muted">
                  {t("added", { date: format.dateTime(new Date(device.createdAt), { dateStyle: "medium" }) })}
                  {device.lastSuccessAt && ` · ${t("lastNotified", { date: format.dateTime(new Date(device.lastSuccessAt), { dateStyle: "medium", timeStyle: "short" }) })}`}
                </span>
              </span>
              <button type="button" className={secondaryButton} onClick={() => remove(device.id)} aria-label={t("removeDevice", { device: device.deviceName })}>
                {t("remove")}
              </button>
            </li>
          ))}
        </ul>
      )}
      {status.devices.length > 0 && (
        <button type="button" className={secondaryButton} onClick={test} data-testid="push-test">
          {t("test")}
        </button>
      )}
      <p aria-live="polite" className={message?.ok ? "text-ok" : "text-bad"}>
        {message?.text ?? ""}
      </p>
    </section>
  );
}

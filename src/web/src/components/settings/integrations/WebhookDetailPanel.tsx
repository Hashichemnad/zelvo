"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, deleteRequest, getJson, patchJson, postJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import {
  errorKind,
  prettyJson,
  type Page,
  type Webhook,
  type WebhookDelivery,
  type WebhookSecret,
  type WebhookTestResult,
} from "@/lib/integrations";
import { permissionKey } from "./ApiClientsPanel";
import { SecretReveal } from "./SecretReveal";
import { cellClass, codeClass, dangerButton, primaryButton, secondaryButton, tableClass } from "./ui";

const errorKinds = new Set(["http", "timeout", "connection-failed", "address-not-allowed", "redirect", "url-not-https", "subscription-not-active", "too-many-failures"]);

/**
 * One webhook: the tester, pausing and resuming, a new signing secret, deleting, and the delivery
 * log with "Send again".
 */
export function WebhookDetailPanel({
  initial,
  initialDeliveries,
  csrfToken,
  display,
}: {
  initial: Webhook;
  initialDeliveries: Page<WebhookDelivery>;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("settings.integrations");
  const tw = useTranslations("settings.integrations.webhook");
  const tl = useTranslations("settings.integrations.webhooks");
  const ts = useTranslations("settings.integrations.secret");
  const tCommon = useTranslations("common");
  const router = useRouter();
  const [webhook, setWebhook] = useState(initial);
  const [deliveries, setDeliveries] = useState(initialDeliveries.items);
  const [secret, setSecret] = useState<string | null>(null);
  const [test, setTest] = useState<WebhookTestResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  const date = (value: string | null) => (value ? formatDateTime(new Date(value), display) : t("never"));
  const number = (value: number) => formatNumber(value, display.locale, display.digits);
  const base = `/v1/integrations/webhooks/${webhook.id}`;

  const explain = (error: string | null) => {
    const kind = errorKind(error);
    return kind && errorKinds.has(kind) ? t(`errorKinds.${kind}`) : null;
  };

  async function refresh() {
    setDeliveries((await getJson<Page<WebhookDelivery>>(`${base}/deliveries?pageSize=50`)).items);
  }

  async function act(name: string, action: () => Promise<void>) {
    setBusy(name);
    setMessage(null);
    try {
      await action();
    } catch (error) {
      setMessage({ ok: false, text: error instanceof ApiError && error.rateLimited ? tCommon("rateLimited") : t("failed") });
    } finally {
      setBusy(null);
    }
  }

  const sendTest = () =>
    act("test", async () => {
      setTest(await postJson<WebhookTestResult>(`${base}/test`, undefined, csrfToken));
      await refresh();
    });

  const setStatus = (status: "Active" | "Paused") =>
    act("status", async () => setWebhook(await patchJson<Webhook>(base, { status }, csrfToken)));

  const rotate = () =>
    act("rotate", async () => {
      const result = await postJson<WebhookSecret>(`${base}/rotate-secret`, undefined, csrfToken);
      setWebhook(result.webhook);
      setSecret(result.secret);
    });

  const remove = () =>
    act("delete", async () => {
      await deleteRequest(base, csrfToken);
      router.push("/settings/integrations/webhooks");
    });

  const redeliver = (id: number) =>
    act(`redeliver-${id}`, async () => {
      await postJson(`${base}/deliveries/${id}/redeliver`, undefined, csrfToken);
      setMessage({ ok: true, text: tw("redelivered") });
      await refresh();
    });

  return (
    <div className="space-y-6" data-testid="webhook-detail">
      <dl className="grid max-w-3xl gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{tl("url")}</dt>
        <dd>
          <code dir="ltr" lang="en" className={codeClass}>{webhook.url}</code>
        </dd>
        <dt className="font-semibold">{tl("status")}</dt>
        <dd data-testid="webhook-status" className={webhook.status === "Active" ? "text-ok" : "text-bad"}>
          {tl(`statuses.${webhook.status}`)}
        </dd>
        <dt className="font-semibold">{tl("events")}</dt>
        <dd>
          <ul className="list-disc ps-5">
            {webhook.events.map((type) => (
              <li key={type}>{t(`events.${permissionKey(type)}`)}</li>
            ))}
          </ul>
        </dd>
        <dt className="font-semibold">{tl("failures")}</dt>
        <dd>{number(webhook.consecutiveFailures)}</dd>
      </dl>

      {webhook.status === "Paused" && <p>{tw("pausedText")}</p>}
      {webhook.status === "Disabled" && <p className="text-bad">{tw("disabledText")}</p>}
      {webhook.secretRotating && <p>{tw("rotating", { date: date(webhook.previousSecretExpiresAt) })}</p>}
      {secret && <SecretReveal label={ts("webhookSecret")} value={secret} onDone={() => setSecret(null)} />}

      <div className="flex flex-wrap gap-3">
        <button type="button" onClick={sendTest} disabled={busy !== null} className={primaryButton}>
          {busy === "test" ? tw("testing") : tw("test")}
        </button>
        {webhook.status === "Active" ? (
          <button type="button" onClick={() => setStatus("Paused")} disabled={busy !== null} className={secondaryButton}>
            {tw("pause")}
          </button>
        ) : (
          <button type="button" onClick={() => setStatus("Active")} disabled={busy !== null} className={secondaryButton}>
            {webhook.status === "Disabled" ? tw("enable") : tw("resume")}
          </button>
        )}
        <button type="button" onClick={rotate} disabled={busy !== null} className={secondaryButton}>
          {tw("rotate")}
        </button>
        <button type="button" onClick={() => setConfirmDelete(true)} disabled={busy !== null} className={dangerButton}>
          {tw("delete")}
        </button>
      </div>
      <p className="max-w-3xl text-sm text-ink-muted">{tw("rotateText")} {tw("verifyHelp")}</p>

      {confirmDelete && (
        <div role="group" className="space-y-2 rounded-md border border-bad p-4">
          <p>{tw("deleteConfirmText", { name: webhook.name })}</p>
          <div className="flex flex-wrap gap-2">
            <button type="button" onClick={remove} className={dangerButton}>{tw("deleteConfirm")}</button>
            <button type="button" onClick={() => setConfirmDelete(false)} className={secondaryButton}>{t("cancel")}</button>
          </div>
        </div>
      )}

      <div role="status" aria-live="polite" data-testid="webhook-test-result">
        {test && (
          <div className={`space-y-2 rounded-md border p-4 ${test.succeeded ? "border-ok" : "border-bad"}`}>
            <p className={test.succeeded ? "text-ok" : "text-bad"}>
              {test.succeeded
                ? tw("testOk", { status: test.statusCode ?? 0, ms: number(test.durationMs) })
                : `${tw("testFailed")} ${explain(test.error) ?? ""}`}
            </p>
            {test.error && (
              <code dir="ltr" lang="en" className={codeClass}>{test.error}</code>
            )}
            {test.responseBody && (
              <>
                <p className="font-semibold">{tw("response")}</p>
                <pre dir="ltr" lang="en" className={codeClass}>{prettyJson(test.responseBody)}</pre>
              </>
            )}
          </div>
        )}
        {message && <p className={message.ok ? "text-ok" : "text-bad"}>{message.text}</p>}
      </div>

      <section aria-labelledby="deliveries-heading" className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h3 id="deliveries-heading" className="text-lg font-semibold">{tw("deliveries")}</h3>
          <button type="button" onClick={() => act("refresh", refresh)} className={secondaryButton}>{tw("refresh")}</button>
        </div>
        {deliveries.length === 0 ? (
          <p>{tw("deliveriesEmpty")}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className={tableClass}>
              <thead>
                <tr>
                  <th scope="col" className={cellClass}>{tw("event")}</th>
                  <th scope="col" className={cellClass}>{tl("status")}</th>
                  <th scope="col" className={cellClass}>{tw("attempts")}</th>
                  <th scope="col" className={cellClass}>{tw("lastStatus")}</th>
                  <th scope="col" className={cellClass}>{tw("created")}</th>
                  <th scope="col" className={cellClass}><span className="sr-only">{tw("redeliver")}</span></th>
                </tr>
              </thead>
              <tbody>
                {deliveries.map((delivery) => (
                  <tr key={delivery.id} data-testid="delivery-row">
                    <td className={cellClass}>
                      <span className="block">{t(`events.${permissionKey(delivery.eventType)}`)}</span>
                      {delivery.manual && <span className="block text-xs text-ink-muted">{tw("manual")}</span>}
                      <details className="mt-1">
                        <summary className="cursor-pointer text-sm text-brand">{tw("payload")}</summary>
                        <pre dir="ltr" lang="en" className={codeClass}>{prettyJson(delivery.payload)}</pre>
                      </details>
                    </td>
                    <td className={`${cellClass} ${delivery.status === "Failed" ? "text-bad" : delivery.status === "Succeeded" ? "text-ok" : ""}`}>
                      {tw(`deliveryStatuses.${delivery.status}`)}
                      {delivery.status === "Pending" && delivery.nextAttemptAt && (
                        <span className="block text-xs text-ink-muted">{tw("nextAttempt")}: {date(delivery.nextAttemptAt)}</span>
                      )}
                      {delivery.lastError && <span className="block text-xs">{explain(delivery.lastError)}</span>}
                    </td>
                    <td className={cellClass}>{number(delivery.attempts)}</td>
                    <td className={cellClass}>
                      {delivery.lastStatusCode !== null ? number(delivery.lastStatusCode) : tw("noAnswer")}
                      {delivery.lastDurationMs !== null && (
                        <span className="block text-xs text-ink-muted">{t("log.durationMs", { ms: number(delivery.lastDurationMs) })}</span>
                      )}
                    </td>
                    <td className={cellClass}>{date(delivery.createdAt)}</td>
                    <td className={cellClass}>
                      {delivery.status !== "Pending" && delivery.eventType !== "webhook.test" && (
                        <button type="button" onClick={() => redeliver(delivery.id)} disabled={busy !== null} className={secondaryButton}>
                          {tw("redeliver")}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}

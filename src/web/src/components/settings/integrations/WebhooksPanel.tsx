"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import type { IntegrationInfo, Page, Webhook, WebhookSecret } from "@/lib/integrations";
import { permissionKey } from "./ApiClientsPanel";
import { SecretReveal } from "./SecretReveal";
import { cellClass, inputClass, primaryButton, tableClass } from "./ui";

/** Settings → Integrations → Webhooks: the subscriptions and creating one (its signing secret shown once). */
export function WebhooksPanel({ initial, info, csrfToken }: { initial: Page<Webhook>; info: IntegrationInfo; csrfToken: string }) {
  const t = useTranslations("settings.integrations");
  const tw = useTranslations("settings.integrations.webhooks");
  const ts = useTranslations("settings.integrations.secret");
  const tCommon = useTranslations("common");
  const id = useId();
  const [webhooks, setWebhooks] = useState(initial.items);
  const [secret, setSecret] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [events, setEvents] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (events.length === 0) {
      setError(tw("noEvents"));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const result = await postJson<WebhookSecret>("/v1/integrations/webhooks", { name, url, events }, csrfToken);
      setSecret(result.secret);
      setName("");
      setUrl("");
      setEvents([]);
      setWebhooks((await getJson<Page<Webhook>>("/v1/integrations/webhooks?pageSize=100")).items);
    } catch (refused) {
      setError(problemText(refused));
    } finally {
      setBusy(false);
    }
  }

  // The API says which rule a refused URL broke; anything else is a general failure.
  function problemText(refused: unknown): string {
    if (refused instanceof ApiError && refused.rateLimited) {
      return tCommon("rateLimited");
    }
    const problem = refused instanceof ApiError ? refused.details.urlProblem : undefined;
    if (problem === "url-not-https" || problem === "address-not-allowed" || problem === "url-invalid") {
      return tw(`urlProblems.${problem}`);
    }
    return t("failed");
  }

  return (
    <div className="space-y-6" data-testid="webhooks-panel">
      <p className="max-w-3xl">{tw("intro")}</p>
      {secret && <SecretReveal label={ts("webhookSecret")} value={secret} onDone={() => setSecret(null)} />}

      {webhooks.length === 0 ? (
        <p>{tw("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass} aria-label={tw("listLabel")}>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{tw("name")}</th>
                <th scope="col" className={cellClass}>{tw("url")}</th>
                <th scope="col" className={cellClass}>{tw("events")}</th>
                <th scope="col" className={cellClass}>{tw("status")}</th>
                <th scope="col" className={cellClass}>
                  <span className="sr-only">{tw("details")}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {webhooks.map((webhook) => (
                <tr key={webhook.id} data-testid="webhook-row">
                  <td className={`${cellClass} font-semibold`}>{webhook.name}</td>
                  <td className={cellClass}>
                    <code dir="ltr" lang="en" className="break-all text-xs">{webhook.url}</code>
                  </td>
                  <td className={cellClass}>{tw("eventsCount", { count: webhook.events.length })}</td>
                  <td className={`${cellClass} ${webhook.status === "Active" ? "text-ok" : "text-bad"}`}>{tw(`statuses.${webhook.status}`)}</td>
                  <td className={cellClass}>
                    <Link href={`/settings/integrations/webhooks/${webhook.id}`} className="font-semibold text-brand underline">
                      {tw("details")}
                      <span className="sr-only">: {webhook.name}</span>
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <form onSubmit={submit} className="max-w-3xl space-y-4 rounded-md border border-line p-4" aria-labelledby={`${id}-title`} noValidate>
        <h3 id={`${id}-title`} className="text-lg font-semibold">{tw("createTitle")}</h3>
        <div>
          <label htmlFor={`${id}-name`} className="font-semibold">{tw("name")}</label>
          <input id={`${id}-name`} required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} className={inputClass} />
        </div>
        <div>
          <label htmlFor={`${id}-url`} className="font-semibold">{tw("url")}</label>
          <input
            id={`${id}-url`}
            type="url"
            required
            dir="ltr"
            maxLength={2000}
            placeholder="https://"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            aria-describedby={`${id}-url-help`}
            className={inputClass}
          />
          <p id={`${id}-url-help`} className="text-sm text-ink-muted">{tw("urlHelp")}</p>
        </div>
        <fieldset className="space-y-2">
          <legend className="font-semibold">{tw("events")}</legend>
          {info.events.map((type) => (
            <label key={type} className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={events.includes(type)}
                onChange={(e) => setEvents(e.target.checked ? [...events, type] : events.filter((x) => x !== type))}
              />
              <span>{t(`events.${permissionKey(type)}`)}</span>
              <code dir="ltr" lang="en" className="text-xs text-ink-muted">{type}</code>
            </label>
          ))}
        </fieldset>
        <button type="submit" disabled={busy || !name || !url} className={primaryButton}>
          {busy ? tw("creating") : tw("create")}
        </button>
        <p role="status" aria-live="polite" className="text-bad">{error ?? ""}</p>
      </form>
    </div>
  );
}


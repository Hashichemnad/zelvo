"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { formatUsd, type AiPrice, type AiProvider } from "@/lib/ai";
import { ApiError, deleteRequest, getJson, putJson } from "@/lib/client-api";
import type { DateDisplayOptions } from "@/lib/format";
import { cellClass, dangerButton, inputClass, primaryButton, tableClass } from "../integrations/ui";

/** Settings → AI → Prices: US dollars per million tokens for each provider's model, entered by the administrator. */
export function AiPricesPanel({ initial, providers, csrfToken, display }: { initial: AiPrice[]; providers: AiProvider[]; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("settings.ai.prices");
  const tAi = useTranslations("settings.ai");
  const id = useId();
  const [prices, setPrices] = useState(initial);
  const [providerId, setProviderId] = useState(providers[0]?.id ?? "");
  const [model, setModel] = useState("");
  const [input, setInput] = useState("");
  const [output, setOutput] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const usd = (value: number) => formatUsd(value, display.locale, display.digits);
  const providerName = (pid: string) => providers.find((p) => p.id === pid)?.name ?? pid;

  async function reload() {
    setPrices(await getJson<AiPrice[]>("/v1/ai/prices"));
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    const inputPerMillion = Number(input);
    const outputPerMillion = Number(output);
    if (input.trim() === "" || output.trim() === "" || !Number.isFinite(inputPerMillion) || !Number.isFinite(outputPerMillion)) {
      setError(t("invalid"));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await putJson("/v1/ai/prices", { providerId, model, inputPerMillion, outputPerMillion }, csrfToken);
      setModel("");
      setInput("");
      setOutput("");
      await reload();
    } catch (refused) {
      setError(refused instanceof ApiError && refused.rateLimited ? tAi("rateLimited") : refused instanceof ApiError && refused.status === 400 ? t("invalid") : tAi("failed"));
    } finally {
      setBusy(false);
    }
  }

  async function remove(price: AiPrice) {
    setError(null);
    try {
      await deleteRequest(`/v1/ai/prices?${new URLSearchParams({ providerId: price.providerId, model: price.model })}`, csrfToken);
      await reload();
    } catch {
      setError(tAi("failed"));
    }
  }

  return (
    <div className="space-y-6" data-testid="ai-prices-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {prices.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass} aria-label={t("listLabel")}>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("provider")}</th>
                <th scope="col" className={cellClass}>{t("model")}</th>
                <th scope="col" className={cellClass}>{t("input")}</th>
                <th scope="col" className={cellClass}>{t("output")}</th>
                <th scope="col" className={cellClass}><span className="sr-only">{t("remove")}</span></th>
              </tr>
            </thead>
            <tbody>
              {prices.map((price) => (
                <tr key={`${price.providerId}/${price.model}`} data-testid="ai-price-row">
                  <td className={cellClass}>{providerName(price.providerId)}</td>
                  <td className={cellClass}><code dir="ltr" lang="en">{price.model}</code></td>
                  <td className={cellClass}>{usd(price.inputPerMillion)}</td>
                  <td className={cellClass}>{usd(price.outputPerMillion)}</td>
                  <td className={cellClass}>
                    <button type="button" onClick={() => remove(price)} className={dangerButton}>
                      {t("remove")}
                      <span className="sr-only">: {price.model}</span>
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {providers.length === 0 ? (
        <p role="note">{t("noProviders")}</p>
      ) : (
        <form onSubmit={submit} className="max-w-3xl space-y-4 rounded-md border border-line p-4" aria-labelledby={`${id}-title`} noValidate>
          <h3 id={`${id}-title`} className="text-lg font-semibold">{t("setTitle")}</h3>
          <div className="grid gap-3 sm:grid-cols-2">
            <div>
              <label htmlFor={`${id}-provider`} className="font-semibold">{t("provider")}</label>
              <select id={`${id}-provider`} value={providerId} onChange={(e) => setProviderId(e.target.value)} className={inputClass}>
                {providers.map((p) => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </select>
            </div>
            <div>
              <label htmlFor={`${id}-model`} className="font-semibold">{t("model")}</label>
              <input id={`${id}-model`} dir="ltr" required maxLength={200} value={model} onChange={(e) => setModel(e.target.value)} className={inputClass} />
            </div>
            <div>
              <label htmlFor={`${id}-input`} className="font-semibold">{t("input")}</label>
              <input id={`${id}-input`} dir="ltr" inputMode="decimal" required value={input} onChange={(e) => setInput(e.target.value)} className={inputClass} />
            </div>
            <div>
              <label htmlFor={`${id}-output`} className="font-semibold">{t("output")}</label>
              <input id={`${id}-output`} dir="ltr" inputMode="decimal" required value={output} onChange={(e) => setOutput(e.target.value)} className={inputClass} />
            </div>
          </div>
          <p className="text-sm text-ink-muted">{t("help")}</p>
          {error && <p role="alert" className="text-bad">{error}</p>}
          <button type="submit" disabled={busy || !model.trim()} className={primaryButton}>{t("save")}</button>
        </form>
      )}
    </div>
  );
}

"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import type { AiFeature, AiProvider } from "@/lib/ai";
import { ApiError, putJson } from "@/lib/client-api";
import { inputClass, primaryButton } from "../integrations/ui";
import { refusedField } from "./problems";

/** Settings → AI → Features: a switch, provider and model for each AI feature ("model chosen per feature"). */
export function AiFeaturesPanel({ initial, providers, csrfToken }: { initial: AiFeature[]; providers: AiProvider[]; csrfToken: string }) {
  const t = useTranslations("settings.ai.features");
  return (
    <div className="space-y-6" data-testid="ai-features-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {providers.length === 0 && <p role="note" className="font-semibold">{t("noProviders")}</p>}
      <ul className="space-y-4">
        {initial.map((feature) => (
          <li key={feature.feature}>
            <FeatureForm feature={feature} providers={providers} csrfToken={csrfToken} />
          </li>
        ))}
      </ul>
    </div>
  );
}

function FeatureForm({ feature, providers, csrfToken }: { feature: AiFeature; providers: AiProvider[]; csrfToken: string }) {
  const t = useTranslations("settings.ai.features");
  const tNames = useTranslations("settings.ai.featureNames");
  const tAi = useTranslations("settings.ai");
  const id = useId();
  const [enabled, setEnabled] = useState(feature.enabled);
  const [providerId, setProviderId] = useState(feature.providerId ?? "");
  const [model, setModel] = useState(feature.model ?? "");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const active = providers.filter((p) => p.status === "Active" || p.id === feature.providerId);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      await putJson(`/v1/ai/features/${feature.feature}`, { enabled, providerId: providerId || null, model: model || null }, csrfToken);
      setMessage({ ok: true, text: t("saved") });
    } catch (refused) {
      const { field } = refusedField(refused);
      setMessage({
        ok: false,
        text:
          refused instanceof ApiError && refused.rateLimited
            ? tAi("rateLimited")
            : field === "enabled"
              ? t("needsProviderAndModel")
              : field === "providerId"
                ? t("providerOff")
                : field === "model"
                  ? t("modelInvalid")
                  : tAi("failed"),
      });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="space-y-3 rounded-md border border-line p-4" aria-labelledby={`${id}-title`} data-testid={`ai-feature-${feature.feature}`} noValidate>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id={`${id}-title`} className="text-lg font-semibold">{tNames(feature.feature)}</h3>
        <label className="flex items-center gap-2 font-semibold">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
          {t("enabled")}
        </label>
      </div>
      <p className="text-sm text-ink-muted">{t(`descriptions.${feature.feature}`)}</p>
      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label htmlFor={`${id}-provider`} className="font-semibold">{t("provider")}</label>
          <select id={`${id}-provider`} value={providerId} onChange={(e) => setProviderId(e.target.value)} className={inputClass}>
            <option value="">{t("noProvider")}</option>
            {active.map((p) => (
              <option key={p.id} value={p.id}>{p.name}</option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor={`${id}-model`} className="font-semibold">{t("model")}</label>
          <input id={`${id}-model`} dir="ltr" maxLength={200} value={model} onChange={(e) => setModel(e.target.value)} className={inputClass} />
        </div>
      </div>
      {message && (
        <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-bad"}>
          {message.text}
        </p>
      )}
      <button type="submit" disabled={busy} className={primaryButton}>
        {t("save")}
        <span className="sr-only">: {tNames(feature.feature)}</span>
      </button>
    </form>
  );
}

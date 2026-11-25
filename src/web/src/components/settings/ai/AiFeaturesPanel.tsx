"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import type { AiFeature, AiProvider } from "@/lib/ai";
import type { TriageAccuracy } from "@/lib/ai-features";
import { ApiError, putJson } from "@/lib/client-api";
import { inputClass, primaryButton } from "../integrations/ui";
import { refusedField } from "./problems";

/**
 * Settings → AI → Features: a switch, provider and model for each AI feature ("model chosen per
 * feature"). Task E5: triage also has the rule "apply automatically to new incidents" (off by
 * default) and how often agents kept its suggestions.
 */
export function AiFeaturesPanel({
  initial,
  providers,
  csrfToken,
  triage,
}: {
  initial: AiFeature[];
  providers: AiProvider[];
  csrfToken: string;
  triage?: { autoApply: boolean; accuracy: TriageAccuracy | null };
}) {
  const t = useTranslations("settings.ai.features");
  return (
    <div className="space-y-6" data-testid="ai-features-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {providers.length === 0 && <p role="note" className="font-semibold">{t("noProviders")}</p>}
      <ul className="space-y-4">
        {initial.map((feature) => (
          <li key={feature.feature} className="space-y-2">
            <FeatureForm feature={feature} providers={providers} csrfToken={csrfToken} />
            {feature.feature === "triage" && triage && <TriageRule initial={triage.autoApply} accuracy={triage.accuracy} csrfToken={csrfToken} />}
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

/** The administrator's rule for triage (product owner decision 2026-11-24) and how its suggestions fare. */
function TriageRule({ initial, accuracy, csrfToken }: { initial: boolean; accuracy: TriageAccuracy | null; csrfToken: string }) {
  const t = useTranslations("settings.ai.triage");
  const tAi = useTranslations("settings.ai");
  const id = useId();
  const [autoApply, setAutoApply] = useState(initial);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  async function change(next: boolean) {
    setMessage(null);
    try {
      await putJson("/v1/ai/triage/settings", { autoApply: next }, csrfToken);
      setAutoApply(next);
      setMessage({ ok: true, text: t("saved") });
    } catch {
      setMessage({ ok: false, text: tAi("failed") });
    }
  }

  const percent = (value: number | null) => (value === null ? "—" : `${value} %`);
  return (
    <section aria-labelledby={`${id}-title`} className="space-y-2 rounded-md border border-dashed border-line p-4" data-testid="ai-triage-rule">
      <h4 id={`${id}-title`} className="font-semibold">{t("title")}</h4>
      <label className="flex items-start gap-2">
        <input type="checkbox" checked={autoApply} onChange={(e) => void change(e.target.checked)} aria-describedby={`${id}-help`} className="mt-1" />
        <span>{t("autoApply")}</span>
      </label>
      <p id={`${id}-help`} className="text-sm text-ink-muted">{t("autoApplyHelp")}</p>
      <p aria-live="polite" className={message?.ok === false ? "text-bad" : "text-ink-muted"}>{message?.text ?? ""}</p>
      {accuracy && accuracy.total > 0 && (
        <>
          <h5 className="font-semibold">{t("accuracyTitle", { days: accuracy.days })}</h5>
          <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm sm:grid-cols-4">
            {(["accepted", "corrected", "dismissed", "autoApplied"] as const).map((key) => (
              <div key={key}>
                <dt className="text-ink-muted">{t(`counts.${key}`)}</dt>
                <dd className="font-semibold">{accuracy[key]}</dd>
              </div>
            ))}
            <div>
              <dt className="text-ink-muted">{t("kept.category")}</dt>
              <dd className="font-semibold" dir="ltr">{percent(accuracy.categoryKeptPercent)}</dd>
            </div>
            <div>
              <dt className="text-ink-muted">{t("kept.priority")}</dt>
              <dd className="font-semibold" dir="ltr">{percent(accuracy.priorityKeptPercent)}</dd>
            </div>
            <div>
              <dt className="text-ink-muted">{t("kept.group")}</dt>
              <dd className="font-semibold" dir="ltr">{percent(accuracy.groupKeptPercent)}</dd>
            </div>
          </dl>
          <p className="text-sm text-ink-muted">{t("learning")}</p>
        </>
      )}
    </section>
  );
}

"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { aiProviderKinds, endpointRequired, keyOptional, type AiModels, type AiProvider, type AiProviderKind, type AiTestResult } from "@/lib/ai";
import { ApiError, getJson, patchJson, postJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { codeClass, inputClass, primaryButton, secondaryButton } from "../integrations/ui";
import { errorKey, refusedField } from "./problems";

/** Settings → AI → Providers: connections with encrypted keys, the test button, switching off. */
export function AiProvidersPanel({ initial, csrfToken, display }: { initial: AiProvider[]; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("settings.ai.providers");
  const [providers, setProviders] = useState(initial);
  const [editing, setEditing] = useState<string | null>(null);

  async function reload() {
    setProviders(await getJson<AiProvider[]>("/v1/ai/providers"));
  }

  return (
    <div className="space-y-6" data-testid="ai-providers-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {providers.length === 0 ? (
        <p data-testid="ai-providers-empty">{t("empty")}</p>
      ) : (
        <ul className="space-y-4" aria-label={t("listLabel")}>
          {providers.map((provider) => (
            <li key={provider.id} data-testid="ai-provider">
              <ProviderCard
                provider={provider}
                csrfToken={csrfToken}
                display={display}
                editing={editing === provider.id}
                onEdit={(on) => setEditing(on ? provider.id : null)}
                onChanged={async () => {
                  setEditing(null);
                  await reload();
                }}
              />
            </li>
          ))}
        </ul>
      )}
      <ProviderForm csrfToken={csrfToken} onSaved={reload} />
    </div>
  );
}

function ProviderCard({
  provider,
  csrfToken,
  display,
  editing,
  onEdit,
  onChanged,
}: {
  provider: AiProvider;
  csrfToken: string;
  display: DateDisplayOptions;
  editing: boolean;
  onEdit: (on: boolean) => void;
  onChanged: () => Promise<void>;
}) {
  const t = useTranslations("settings.ai.providers");
  const tErrors = useTranslations("settings.ai.errors");
  const tAi = useTranslations("settings.ai");
  const id = useId();
  const [model, setModel] = useState("");
  const [models, setModels] = useState<string[] | null>(null);
  const [testing, setTesting] = useState(false);
  const [result, setResult] = useState<AiTestResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const date = (value: string) => formatDateTime(new Date(value), display);
  const number = (value: number) => formatNumber(value, display.locale, display.digits);

  async function test(event: FormEvent) {
    event.preventDefault();
    setTesting(true);
    setError(null);
    setResult(null);
    try {
      setResult(await postJson<AiTestResult>(`/v1/ai/providers/${provider.id}/test`, { model }, csrfToken));
      await onChanged();
    } catch (refused) {
      setError(refused instanceof ApiError && refused.rateLimited ? tAi("rateLimited") : tAi("failed"));
    } finally {
      setTesting(false);
    }
  }

  async function loadModels() {
    setError(null);
    try {
      const listed = await getJson<AiModels>(`/v1/ai/providers/${provider.id}/models`);
      setModels(listed.models);
      if (listed.errorCode) {
        setError(tErrors(errorKey(listed.errorCode)));
      }
    } catch {
      setError(tAi("failed"));
    }
  }

  async function setStatus(status: "Active" | "Disabled") {
    setError(null);
    try {
      await patchJson(`/v1/ai/providers/${provider.id}`, { status }, csrfToken);
      await onChanged();
    } catch {
      setError(tAi("failed"));
    }
  }

  return (
    <article className="space-y-3 rounded-md border border-line p-4" aria-labelledby={`${id}-name`}>
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h3 id={`${id}-name`} className="text-lg font-semibold">
          {provider.name}
        </h3>
        <span className={provider.status === "Active" ? "font-semibold text-ok" : "font-semibold text-bad"}>{t(`statuses.${provider.status}`)}</span>
      </div>
      <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{t("kind")}</dt>
        <dd>{t(`kinds.${provider.kind}`)}</dd>
        {provider.endpoint && (
          <>
            <dt className="font-semibold">{t("endpoint")}</dt>
            <dd>
              <code dir="ltr" lang="en" className="break-all">{provider.endpoint}</code>
            </dd>
          </>
        )}
        {provider.apiVersion && (
          <>
            <dt className="font-semibold">{t("apiVersion")}</dt>
            <dd dir="ltr" lang="en" className="text-start">{provider.apiVersion}</dd>
          </>
        )}
        <dt className="font-semibold">{t("apiKey")}</dt>
        <dd data-testid="ai-provider-key">
          {provider.apiKeySet ? t("keySet", { date: date(provider.apiKeyChangedAt!) }) : t("keyNotSet")}
        </dd>
        <dt className="font-semibold">{t("lastTest")}</dt>
        <dd>
          {provider.lastTestAt
            ? t(provider.lastTestSucceeded ? "lastTestOk" : "lastTestFailed", { date: date(provider.lastTestAt) })
            : t("neverTested")}
        </dd>
      </dl>

      <form onSubmit={test} className="flex flex-wrap items-end gap-2" aria-label={t("testLabel", { name: provider.name })}>
        <div className="min-w-56 grow">
          <label htmlFor={`${id}-model`} className="font-semibold">{t("testModel")}</label>
          <input
            id={`${id}-model`}
            list={models ? `${id}-models` : undefined}
            dir="ltr"
            required
            maxLength={200}
            value={model}
            onChange={(e) => setModel(e.target.value)}
            aria-describedby={`${id}-model-help`}
            className={inputClass}
          />
          {models && (
            <datalist id={`${id}-models`}>
              {models.map((m) => (
                <option key={m} value={m} />
              ))}
            </datalist>
          )}
          <p id={`${id}-model-help`} className="text-sm text-ink-muted">{t("modelHelp")}</p>
        </div>
        <button type="button" onClick={loadModels} className={secondaryButton}>{t("listModels")}</button>
        <button type="submit" disabled={testing || !model.trim()} className={primaryButton}>
          {testing ? t("testing") : t("test")}
        </button>
      </form>
      {models && <p className="text-sm" role="status">{models.length === 0 ? t("noModelsListed") : t("modelsListed", { count: models.length })}</p>}

      {result && (
        <div role="status" data-testid="ai-test-result" className={`rounded-md border p-3 ${result.succeeded ? "border-ok" : "border-bad"}`}>
          {result.succeeded ? (
            <>
              <p className="font-semibold text-ok">{t("testOk", { ms: number(result.durationMs) })}</p>
              <p className="text-sm">{t("testTokens", { input: number(result.inputTokens), output: number(result.outputTokens) })}</p>
              <code dir="auto" className={codeClass}>{result.answer}</code>
            </>
          ) : (
            <>
              <p className="font-semibold text-bad">{tErrors(errorKey(result.errorCode))}</p>
              {result.error && <code dir="ltr" lang="en" className={codeClass}>{result.error}</code>}
            </>
          )}
        </div>
      )}
      {error && <p role="alert" className="text-bad">{error}</p>}

      <div className="flex flex-wrap gap-2">
        <button type="button" onClick={() => onEdit(!editing)} aria-expanded={editing} className={secondaryButton}>
          {t("edit")}
        </button>
        {provider.status === "Active" ? (
          <button type="button" onClick={() => setStatus("Disabled")} className={secondaryButton}>{t("disable")}</button>
        ) : (
          <button type="button" onClick={() => setStatus("Active")} className={secondaryButton}>{t("enable")}</button>
        )}
      </div>
      {editing && <ProviderForm csrfToken={csrfToken} existing={provider} onSaved={onChanged} />}
    </article>
  );
}

/** Adds a provider, or changes one (the key is only ever written, never shown). */
function ProviderForm({ csrfToken, existing, onSaved }: { csrfToken: string; existing?: AiProvider; onSaved: () => Promise<void> }) {
  const t = useTranslations("settings.ai.providers");
  const tAi = useTranslations("settings.ai");
  const id = useId();
  const [kind, setKind] = useState<AiProviderKind>(existing?.kind ?? "Anthropic");
  const [name, setName] = useState(existing?.name ?? "");
  const [endpoint, setEndpoint] = useState(existing?.endpoint ?? "");
  const [apiVersion, setApiVersion] = useState(existing?.apiVersion ?? "");
  const [apiKey, setApiKey] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      if (existing) {
        const body: Record<string, string> = { name };
        if (endpoint !== (existing.endpoint ?? "")) body.endpoint = endpoint;
        if (apiVersion !== (existing.apiVersion ?? "")) body.apiVersion = apiVersion;
        if (apiKey) body.apiKey = apiKey;
        await patchJson(`/v1/ai/providers/${existing.id}`, body, csrfToken);
      } else {
        await postJson("/v1/ai/providers", { name, kind, endpoint: endpoint || undefined, apiVersion: apiVersion || undefined, apiKey: apiKey || undefined }, csrfToken);
        setName("");
        setEndpoint("");
        setApiVersion("");
      }
      setApiKey("");
      setSaved(true);
      await onSaved();
    } catch (refused) {
      const { field, endpointProblem } = refusedField(refused);
      setError(
        refused instanceof ApiError && refused.rateLimited
          ? tAi("rateLimited")
          : endpointProblem
            ? t(`endpointProblems.${endpointProblem}`)
            : field === "apiKey"
              ? t(existing && endpoint !== (existing.endpoint ?? "") ? "keyAgain" : "keyInvalid")
              : field
                ? t("fieldInvalid", { field: t(fieldLabel(field)) })
                : tAi("failed"),
      );
    } finally {
      setBusy(false);
    }
  }

  const needsEndpoint = endpointRequired(kind);
  const title = existing ? t("editTitle", { name: existing.name }) : t("createTitle");

  return (
    <form onSubmit={submit} className="max-w-3xl space-y-4 rounded-md border border-line p-4" aria-labelledby={`${id}-title`} noValidate>
      <h3 id={`${id}-title`} className="text-lg font-semibold">{title}</h3>
      {!existing && (
        <div>
          <label htmlFor={`${id}-kind`} className="font-semibold">{t("kind")}</label>
          <select id={`${id}-kind`} value={kind} onChange={(e) => setKind(e.target.value as AiProviderKind)} aria-describedby={`${id}-kind-help`} className={inputClass}>
            {aiProviderKinds.map((k) => (
              <option key={k} value={k}>{t(`kinds.${k}`)}</option>
            ))}
          </select>
          <p id={`${id}-kind-help`} className="text-sm text-ink-muted">{t(`kindHelp.${kind}`)}</p>
        </div>
      )}
      <div>
        <label htmlFor={`${id}-name`} className="font-semibold">{t("name")}</label>
        <input id={`${id}-name`} required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} className={inputClass} />
      </div>
      <div>
        <label htmlFor={`${id}-endpoint`} className="font-semibold">
          {needsEndpoint ? t("endpoint") : t("endpointOptional")}
        </label>
        <input
          id={`${id}-endpoint`}
          type="url"
          dir="ltr"
          required={needsEndpoint}
          maxLength={2000}
          placeholder="https://"
          value={endpoint}
          onChange={(e) => setEndpoint(e.target.value)}
          aria-describedby={`${id}-endpoint-help`}
          className={inputClass}
        />
        <p id={`${id}-endpoint-help`} className="text-sm text-ink-muted">{t(`endpointHelp.${kind}`)}</p>
      </div>
      {kind === "AzureOpenAI" && (
        <div>
          <label htmlFor={`${id}-version`} className="font-semibold">{t("apiVersionOptional")}</label>
          <input id={`${id}-version`} dir="ltr" maxLength={40} value={apiVersion} onChange={(e) => setApiVersion(e.target.value)} aria-describedby={`${id}-version-help`} className={inputClass} />
          <p id={`${id}-version-help`} className="text-sm text-ink-muted">{t("apiVersionHelp")}</p>
        </div>
      )}
      <div>
        <label htmlFor={`${id}-key`} className="font-semibold">
          {existing ? t("newKey") : keyOptional(kind) ? t("apiKeyOptional") : t("apiKey")}
        </label>
        <input
          id={`${id}-key`}
          type="password"
          autoComplete="off"
          dir="ltr"
          maxLength={1000}
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
          aria-describedby={`${id}-key-help`}
          className={inputClass}
        />
        <p id={`${id}-key-help`} className="text-sm text-ink-muted">{t(existing ? "newKeyHelp" : "apiKeyHelp")}</p>
      </div>
      {error && <p role="alert" className="text-bad">{error}</p>}
      {saved && <p role="status" className="text-ok">{t("saved")}</p>}
      <button type="submit" disabled={busy || !name.trim()} className={primaryButton}>
        {existing ? t("save") : t("create")}
      </button>
    </form>
  );
}

function fieldLabel(field: string): string {
  return ({ name: "name", endpoint: "endpoint", apiVersion: "apiVersion", kind: "kind" } as Record<string, string>)[field] ?? "name";
}

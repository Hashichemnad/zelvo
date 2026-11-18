"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { DisplayFormat } from "@/lib/display";
import { formatDateTime } from "@/lib/format";

export type UaePassEnvironment = "Staging" | "Production" | "Mock";

export type UaePassLevel = "SOP1" | "SOP2" | "SOP3";

const levels: UaePassLevel[] = ["SOP1", "SOP2", "SOP3"];

/** Settings → UAE PASS as the API returns them (GET /api/v1/identity/uae-pass). */
export interface UaePassSettings {
  enabled: boolean;
  environment: UaePassEnvironment;
  clientId: string;
  clientSecretSet: boolean;
  clientSecretChangedAt: string | null;
  redirectUri: string;
  environments: UaePassEnvironment[];
  acceptAsSecondFactor: boolean;
  requesterMinimumLevel: UaePassLevel;
  staffMinimumLevel: UaePassLevel;
  createRequesters: boolean;
  updatedAt: string | null;
}

type ErrorKey = "clientId" | "clientSecret" | "environment" | "unavailable";

const checkboxClass = "mt-1 size-5 shrink-0 accent-brand";

const inputClass =
  "w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand aria-[invalid=true]:border-bad";

/** Field errors from a validation problem, as translation keys (the API's English text is not shown). */
function errorKeys(problem: unknown): ErrorKey[] {
  const errors = (problem as { errors?: Record<string, unknown> } | null)?.errors ?? {};
  const known: ErrorKey[] = ["clientId", "clientSecret", "environment"];
  return known.filter((key) => Object.keys(errors).some((field) => field.toLowerCase() === key.toLowerCase()));
}

/**
 * Settings → Sign-in → UAE PASS (GOV-1): on/off, environment, client ID and client secret, and
 * the rules for staff and requesters (product owner decisions 2026-11-18). The secret is
 * write-only: the form never receives it, and an empty field keeps the saved one.
 */
export function UaePassSettingsForm({
  initial,
  csrfToken,
  display,
}: {
  initial: UaePassSettings;
  csrfToken: string;
  display: DisplayFormat;
}) {
  const t = useTranslations("settings.uaePass");
  const locale = useLocale();
  const id = useId();
  const [settings, setSettings] = useState(initial);
  const [enabled, setEnabled] = useState(initial.enabled);
  const [environment, setEnvironment] = useState(initial.environment);
  const [clientId, setClientId] = useState(initial.clientId);
  const [clientSecret, setClientSecret] = useState("");
  const [acceptAsSecondFactor, setAcceptAsSecondFactor] = useState(initial.acceptAsSecondFactor);
  const [requesterMinimumLevel, setRequesterMinimumLevel] = useState(initial.requesterMinimumLevel);
  const [createRequesters, setCreateRequesters] = useState(initial.createRequesters);
  const [status, setStatus] = useState<"idle" | "saving" | "saved" | "failed">("idle");
  const [errors, setErrors] = useState<ErrorKey[]>([]);

  const edited = () => {
    setStatus("idle");
    setErrors([]);
  };

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setStatus("saving");
    setErrors([]);
    try {
      const response = await fetch("/bff/api/v1/identity/uae-pass", {
        method: "PUT",
        headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrfToken },
        body: JSON.stringify({
          enabled,
          environment,
          clientId,
          ...(clientSecret ? { clientSecret } : {}),
          acceptAsSecondFactor,
          requesterMinimumLevel,
          createRequesters,
        }),
      });
      if (response.ok) {
        const saved = (await response.json()) as UaePassSettings;
        setSettings(saved);
        setClientSecret("");
        setStatus("saved");
        return;
      }
      const problem = await response.json().catch(() => null);
      setErrors(response.status === 400 ? errorKeys(problem) : response.status === 502 ? ["unavailable"] : []);
      setStatus("failed");
    } catch {
      setStatus("failed");
    }
  }

  const invalid = (key: ErrorKey) => errors.includes(key);
  const describedBy = (key: ErrorKey, hint?: string) =>
    [hint, invalid(key) ? `${id}-${key}-error` : undefined].filter(Boolean).join(" ") || undefined;
  const errorText = (key: ErrorKey) =>
    invalid(key) ? (
      <p id={`${id}-${key}-error`} className="text-sm text-bad">
        {t(`errors.${key}`)}
      </p>
    ) : null;
  const date = (value: string) => formatDateTime(new Date(value), { locale, ...display });

  return (
    <form onSubmit={save} className="max-w-xl space-y-5" data-testid="uae-pass-settings" noValidate>
      <div className="flex items-center gap-3">
        <input
          id={`${id}-enabled`}
          type="checkbox"
          checked={enabled}
          onChange={(event) => {
            setEnabled(event.target.checked);
            edited();
          }}
          className="size-5 accent-brand"
        />
        <label htmlFor={`${id}-enabled`} className="font-semibold">
          {t("enabled")}
        </label>
      </div>

      <div className="space-y-1">
        <label htmlFor={`${id}-environment`} className="block font-semibold">
          {t("environment")}
        </label>
        <select
          id={`${id}-environment`}
          name="environment"
          value={environment}
          aria-invalid={invalid("environment")}
          aria-describedby={describedBy("environment")}
          onChange={(event) => {
            setEnvironment(event.target.value as UaePassEnvironment);
            edited();
          }}
          className={inputClass}
        >
          {settings.environments.map((value) => (
            <option key={value} value={value}>
              {t(`environments.${value}`)}
            </option>
          ))}
        </select>
        {errorText("environment")}
      </div>

      <div className="space-y-1">
        <label htmlFor={`${id}-client-id`} className="block font-semibold">
          {t("clientId")}
        </label>
        <input
          id={`${id}-client-id`}
          name="clientId"
          type="text"
          dir="ltr"
          autoComplete="off"
          spellCheck={false}
          value={clientId}
          aria-invalid={invalid("clientId")}
          aria-describedby={describedBy("clientId")}
          onChange={(event) => {
            setClientId(event.target.value);
            edited();
          }}
          className={inputClass}
        />
        {errorText("clientId")}
      </div>

      <div className="space-y-1">
        <label htmlFor={`${id}-client-secret`} className="block font-semibold">
          {t("clientSecret")}
        </label>
        <input
          id={`${id}-client-secret`}
          name="clientSecret"
          type="password"
          dir="ltr"
          autoComplete="new-password"
          value={clientSecret}
          aria-invalid={invalid("clientSecret")}
          aria-describedby={describedBy("clientSecret", `${id}-client-secret-hint`)}
          onChange={(event) => {
            setClientSecret(event.target.value);
            edited();
          }}
          className={inputClass}
        />
        <p id={`${id}-client-secret-hint`} className="text-sm text-ink-muted">
          {settings.clientSecretSet && settings.clientSecretChangedAt
            ? t("clientSecretSet", { date: date(settings.clientSecretChangedAt) })
            : t("clientSecretMissing")}
        </p>
        {errorText("clientSecret")}
      </div>

      <fieldset className="space-y-4 rounded-lg border border-line p-4">
        <legend className="px-1 font-semibold">{t("rules")}</legend>
        <p className="text-sm">{t("staffMinimum", { level: settings.staffMinimumLevel })}</p>
        <div className="flex items-start gap-3">
          <input
            id={`${id}-second-factor`}
            type="checkbox"
            checked={acceptAsSecondFactor}
            aria-describedby={`${id}-second-factor-hint`}
            onChange={(event) => {
              setAcceptAsSecondFactor(event.target.checked);
              edited();
            }}
            className={checkboxClass}
          />
          <div>
            <label htmlFor={`${id}-second-factor`} className="font-semibold">
              {t("acceptAsSecondFactor")}
            </label>
            <p id={`${id}-second-factor-hint`} className="text-sm text-ink-muted">
              {t("acceptAsSecondFactorHint")}
            </p>
          </div>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-requester-minimum`} className="block font-semibold">
            {t("requesterMinimumLevel")}
          </label>
          <select
            id={`${id}-requester-minimum`}
            name="requesterMinimumLevel"
            value={requesterMinimumLevel}
            onChange={(event) => {
              setRequesterMinimumLevel(event.target.value as UaePassLevel);
              edited();
            }}
            className={inputClass}
          >
            {levels.map((level) => (
              <option key={level} value={level}>
                {t(`levels.${level}`)}
              </option>
            ))}
          </select>
        </div>
        <div className="flex items-start gap-3">
          <input
            id={`${id}-create-requesters`}
            type="checkbox"
            checked={createRequesters}
            aria-describedby={`${id}-create-requesters-hint`}
            onChange={(event) => {
              setCreateRequesters(event.target.checked);
              edited();
            }}
            className={checkboxClass}
          />
          <div>
            <label htmlFor={`${id}-create-requesters`} className="font-semibold">
              {t("createRequesters")}
            </label>
            <p id={`${id}-create-requesters-hint`} className="text-sm text-ink-muted">
              {t("createRequestersHint")}
            </p>
          </div>
        </div>
      </fieldset>

      <div className="space-y-1">
        <p className="font-semibold">{t("redirectUri")}</p>
        <code dir="ltr" className="block break-all rounded-md bg-surface-muted px-3 py-2 text-sm" data-testid="uae-pass-redirect-uri">
          {settings.redirectUri}
        </code>
        <p className="text-sm text-ink-muted">{t("redirectUriHint")}</p>
      </div>

      <div className="flex flex-wrap items-center gap-4">
        <button
          type="submit"
          disabled={status === "saving"}
          className="rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60"
        >
          {status === "saving" ? t("saving") : t("save")}
        </button>
        <p role="status" aria-live="polite" className={status === "failed" ? "text-bad" : "text-ok"}>
          {status === "saved"
            ? t("saved")
            : status === "failed"
              ? invalid("unavailable")
                ? t("errors.unavailable")
                : t("failed")
              : ""}
        </p>
      </div>
      <p className="text-sm text-ink-muted">
        {settings.updatedAt ? t("lastChanged", { date: date(settings.updatedAt) }) : t("neverChanged")}
      </p>
    </form>
  );
}

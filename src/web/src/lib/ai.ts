/** Settings → AI (module 9): the shapes the API returns. */

import { displayLocale } from "./format";

export type AiProviderKind = "Anthropic" | "OpenAI" | "AzureOpenAI" | "Gemini" | "OpenAICompatible";
export type AiProviderStatus = "Active" | "Disabled";
export type AiCallOutcome = "Succeeded" | "Failed" | "Rejected" | "LimitReached";
export type AiBudgetState = "NoLimit" | "Ok" | "Warning" | "Reached";

export const aiProviderKinds: readonly AiProviderKind[] = ["Anthropic", "OpenAI", "AzureOpenAI", "Gemini", "OpenAICompatible"];

/** Feature keys in Settings order (Zelvo.Ai.AiFeatures). */
export const aiFeatureKeys = ["triage", "summary", "reply", "translation", "duplicates", "sentiment", "virtualAgent", "articleDrafts", "transcription", "ruleBuilder"] as const;
export type AiFeatureKey = (typeof aiFeatureKeys)[number];

/** Whether a provider kind needs an endpoint, and whether it can work without a key. */
export function endpointRequired(kind: AiProviderKind): boolean {
  return kind === "AzureOpenAI" || kind === "OpenAICompatible";
}

export function keyOptional(kind: AiProviderKind): boolean {
  return kind === "OpenAICompatible";
}

export interface AiProvider {
  id: string;
  name: string;
  kind: AiProviderKind;
  endpoint: string | null;
  apiVersion: string | null;
  status: AiProviderStatus;
  apiKeySet: boolean;
  apiKeyChangedAt: string | null;
  lastTestAt: string | null;
  lastTestSucceeded: boolean | null;
  lastTestError: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface AiTestResult {
  succeeded: boolean;
  answer: string | null;
  errorCode: string | null;
  error: string | null;
  durationMs: number;
  inputTokens: number;
  outputTokens: number;
  callId: number | null;
}

export interface AiModels {
  models: string[];
  errorCode: string | null;
  error: string | null;
}

export interface AiFeature {
  feature: AiFeatureKey;
  enabled: boolean;
  providerId: string | null;
  model: string | null;
  updatedAt: string | null;
}

export interface AiPrice {
  providerId: string;
  model: string;
  inputPerMillion: number;
  outputPerMillion: number;
  updatedAt: string;
}

export interface AiBudget {
  monthlyLimitUsd: number;
  spentUsd: number;
  percent: number | null;
  state: AiBudgetState;
  warningPercent: number;
  periodStart: string;
  periodEnd: string;
}

export interface AiUsageTotals {
  key: string;
  calls: number;
  failedCalls: number;
  inputTokens: number;
  outputTokens: number;
  costUsd: number;
}

export interface AiModelUsage {
  providerId: string | null;
  providerName: string | null;
  model: string | null;
  totals: AiUsageTotals;
  unpricedCalls: number;
}

export interface AiUsage {
  month: string;
  periodStart: string;
  periodEnd: string;
  totals: AiUsageTotals;
  unpricedCalls: number;
  budget: AiBudget | null;
  days: AiUsageTotals[];
  features: AiUsageTotals[];
  models: AiModelUsage[];
}

export interface AiCall {
  id: number;
  occurredAt: string;
  feature: string;
  providerId: string | null;
  providerName: string | null;
  providerKind: AiProviderKind | null;
  model: string | null;
  userId: string | null;
  userName: string | null;
  entityType: string | null;
  entityId: string | null;
  outcome: AiCallOutcome;
  errorCode: string | null;
  error: string | null;
  inputTokens: number;
  outputTokens: number;
  costUsd: number | null;
  durationMs: number;
  injectionSuspected: boolean;
  maskedValues: number;
  correlationId: string | null;
}

export interface AiCallDetail {
  call: AiCall;
  prompt: string | null;
  response: string | null;
  textExpired: boolean;
}

/** A US dollar amount in the user's language and digits. */
export function formatUsd(value: number, locale: string, digits: "latn" | "arab", fractionDigits = 2): string {
  return new Intl.NumberFormat(displayLocale(locale), {
    numberingSystem: digits,
    style: "currency",
    currency: "USD",
    minimumFractionDigits: fractionDigits,
    maximumFractionDigits: Math.max(fractionDigits, 4),
  }).format(value);
}

/** Translation key of a feature or "test". */
export function featureLabelKey(feature: string): string {
  return (aiFeatureKeys as readonly string[]).includes(feature) || feature === "test" ? feature : "unknown";
}

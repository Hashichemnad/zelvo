/**
 * Types of Settings → Integrations (module 14), as the API sends them: API keys and OAuth
 * clients, webhooks and their deliveries, the integration log, and bulk import and export.
 */
import type { LocalizedText } from "./api";

export type ApiClientKind = "ApiKey" | "OAuthClient";

export interface ApiClient {
  id: string;
  kind: ApiClientKind;
  name: string;
  description: string | null;
  role: string | null;
  scopes: string[];
  rateLimitPerMinute: number;
  status: "Active" | "Revoked";
  expired: boolean;
  expiresAt: string | null;
  keyHint: string | null;
  clientId: string | null;
  tokenUrl: string | null;
  createdAt: string;
  updatedAt: string;
  rotatedAt: string | null;
  revokedAt: string | null;
  lastUsedAt: string | null;
}

/** An integration with its key or secret, shown once. */
export interface ApiClientSecret {
  client: ApiClient;
  apiKey: string | null;
  clientSecret: string | null;
}

export interface IntegrationRole {
  key: string;
  name: LocalizedText;
  scopes: string[];
}

export interface IntegrationInfo {
  environment: "production" | "test" | "demo" | "development";
  tokenUrl: string;
  apiKeyHeader: string;
  scopes: string[];
  events: string[];
  logRetentionDays: number;
  defaultRateLimit: number;
  maxRateLimit: number;
  roles: IntegrationRole[];
}

export interface Page<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export type WebhookStatus = "Active" | "Paused" | "Disabled";

export interface Webhook {
  id: string;
  name: string;
  url: string;
  events: string[];
  status: WebhookStatus;
  disabledReason: string | null;
  consecutiveFailures: number;
  secretRotating: boolean;
  previousSecretExpiresAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface WebhookSecret {
  webhook: Webhook;
  secret: string;
}

export interface WebhookTestResult {
  succeeded: boolean;
  statusCode: number | null;
  durationMs: number;
  error: string | null;
  responseBody: string | null;
  deliveryId: number;
}

export interface WebhookDelivery {
  id: number;
  eventId: string;
  eventType: string;
  status: "Pending" | "Succeeded" | "Failed";
  manual: boolean;
  attempts: number;
  nextAttemptAt: string | null;
  lastAttemptAt: string | null;
  lastStatusCode: number | null;
  lastDurationMs: number | null;
  lastError: string | null;
  createdAt: string;
  completedAt: string | null;
  payload: string | null;
}

export interface IntegrationLogEntry {
  id: number;
  occurredAt: string;
  direction: "Incoming" | "Outgoing";
  apiClientId: string | null;
  subscriptionId: string | null;
  name: string | null;
  eventType: string | null;
  method: string;
  target: string;
  statusCode: number | null;
  durationMs: number;
  error: string | null;
  correlationId: string | null;
}

export interface IntegrationLogDetail {
  entry: IntegrationLogEntry;
  requestBody: string | null;
  responseBody: string | null;
}

export interface BulkImportError {
  row: number;
  column: string | null;
  code: string;
  message: string;
}

export interface BulkImportResult {
  dryRun: boolean;
  applied: boolean;
  rows: number;
  created: number;
  updated: number;
  unchanged: number;
  errors: BulkImportError[];
}

/** The code at the start of a delivery error ("http-503: …" → "http"), for a translated explanation. */
export function errorKind(error: string | null): string | null {
  if (!error) {
    return null;
  }
  const code = error.split(":")[0] ?? "";
  return code.startsWith("http-") ? "http" : code;
}

/** Pretty-prints JSON for display; other text is returned as it is. */
export function prettyJson(text: string | null): string {
  if (!text) {
    return "";
  }
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

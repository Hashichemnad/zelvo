import { ApiError } from "@/lib/client-api";

/** Codes the API uses for refused endpoints; each has its own message. */
export const endpointProblems = ["endpoint-required", "url-not-https", "address-not-allowed", "url-invalid"] as const;

/** Provider error codes with their own message in the translation files. */
export const providerErrors = [
  "provider-auth-failed",
  "provider-rate-limited",
  "provider-unavailable",
  "provider-rejected",
  "provider-invalid-response",
  "timeout",
  "address-not-allowed",
  "connection-failed",
  "key-unreadable",
  "invalid-json",
  "limit-reached",
] as const;

/** The first field the API refused, and the endpoint rule it broke, if any. */
export function refusedField(error: unknown): { field: string | null; endpointProblem: string | null } {
  if (!(error instanceof ApiError)) {
    return { field: null, endpointProblem: null };
  }
  const errors = error.details.errors as Record<string, string[]> | undefined;
  const field = errors ? (Object.keys(errors)[0] ?? null) : null;
  const problem = typeof error.details.endpointProblem === "string" ? error.details.endpointProblem : null;
  return { field, endpointProblem: problem };
}

/** Translation key for a provider error code under `settings.ai.errors`. */
export function errorKey(code: string | null): string {
  return code && (providerErrors as readonly string[]).includes(code) ? code : "other";
}

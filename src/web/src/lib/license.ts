/**
 * The installation's license (module 12, task F3) as the API returns it, and module gating for
 * the screens: links to modules the license does not include are hidden (the API refuses them too).
 */
export type LicenseKind = "None" | "Development" | "Demo" | "Production" | "Test";

export type FingerprintPartKey = "installationId" | "databaseId" | "machineId" | "clusterId" | "domain";

export interface LicenseState {
  activated: boolean;
  activationRequired: boolean;
  kind: LicenseKind;
  problem: string | null;
  reason: string | null;
  licenseId: string | null;
  licensedTo: { en: string | null; ar: string | null } | null;
  customerId: string | null;
  licensedVersion: string | null;
  upgrades: string[];
  issuedAt: string | null;
  installedVersion: string;
  releaseBuild: boolean;
  modules: { key: string; licensed: boolean }[];
  fingerprint: { key: FingerprintPartKey; value: string | null; matches: boolean | null }[];
  files: { fileType: "License" | "Upgrade"; licenseId: string; importedAt: string; fileName: string | null }[];
}

/** Module ids as numbered in docs/requirements.md. */
export const modules = {
  incidents: "m01-incidents",
  serviceCatalog: "m02-service-catalog",
  customerVoice: "m03-customer-voice",
  portal: "m04-portal",
  knowledge: "m07-knowledge",
} as const;

/**
 * Whether a module's links show. Without module information (an older API or the API not reachable)
 * nothing is hidden: the API still refuses what the license does not include.
 */
export function moduleShown(licensed: readonly string[] | undefined, module: string): boolean {
  return licensed === undefined || licensed.includes(module);
}

/** Problem codes the license import answers with (urn:zelvo:problem:license-...). */
export const licenseProblems = [
  "not-activated", "fingerprint-mismatch", "kind-mismatch", "watermark-mismatch", "signature-invalid", "unknown-key", "format-invalid",
  "upgrade-mismatch", "store-unavailable", "development-in-production",
] as const;

export type LicenseProblem = (typeof licenseProblems)[number];

/** The problem's key for the translations, or "other". */
export function problemKey(code: string | null | undefined): LicenseProblem | "other" {
  const bare = code?.replace(/^license-/, "") ?? "";
  return (licenseProblems as readonly string[]).includes(bare) ? (bare as LicenseProblem) : "other";
}

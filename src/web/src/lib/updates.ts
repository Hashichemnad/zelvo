/** Settings → Updates (module 13, task F4) as the API returns it. */
export type InstallState = "Scheduled" | "Running" | "Succeeded" | "RolledBack" | "Failed" | "Cancelled";
export type StepState = "Pending" | "Running" | "Done" | "Failed" | "Skipped";

export interface UpdatePackage {
  id: string;
  version: string;
  type: "Security" | "Feature";
  fileName: string;
  sizeBytes: number;
  sha256: string;
  status: "Imported" | "Installed" | "Removed";
  importedAt: string;
  releaseNotes: { en: string | null; ar: string | null };
  expectedMinutes: number;
  installable: boolean;
  problem: string | null;
  reason: string | null;
}

export interface UpdateInstall {
  id: string;
  packageId: string;
  fromVersion: string;
  toVersion: string;
  state: InstallState;
  scheduledFor: string;
  noticeFrom: string;
  expectedMinutes: number;
  steps: { key: string; state: StepState; startedAt: string | null; endedAt: string | null; message: string | null }[];
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface UpdatesState {
  installedVersion: string;
  updaterOnline: boolean;
  updaterSeenAt: string | null;
  packages: UpdatePackage[];
  installs: UpdateInstall[];
  inbox: { fileName: string; sizeBytes: number; modifiedAt: string }[];
  maxPackageBytes: number;
}

export interface Maintenance {
  announced: boolean;
  inProgress: boolean;
  version: string | null;
  scheduledFor: string | null;
  expectedMinutes: number | null;
}

/** Problem codes the import and install answer with. */
export const packageProblems = [
  "package-format-invalid", "package-signature-invalid", "package-unknown-key", "package-file-mismatch", "package-not-newer", "package-type-mismatch",
  "package-upgrade-license-required", "package-installed-too-old", "package-images-missing", "package-not-licensed", "package-too-large", "install-pending",
] as const;

export function packageProblemKey(code: string | null | undefined): (typeof packageProblems)[number] | "other" {
  return (packageProblems as readonly string[]).includes(code ?? "") ? (code as (typeof packageProblems)[number]) : "other";
}

/** An install still to finish: the screen follows it. */
export function isActive(install: UpdateInstall): boolean {
  return install.state === "Scheduled" || install.state === "Running";
}

/** Megabytes, rounded, for file sizes. */
export function megabytes(bytes: number): number {
  return Math.max(1, Math.round(bytes / (1024 * 1024)));
}

/** A `datetime-local` value as an ISO instant in the browser's time zone, or null. */
export function localToIso(value: string): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

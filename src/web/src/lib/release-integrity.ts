import { createHash, verify } from "node:crypto";
import { readdirSync, readFileSync, existsSync, statSync } from "node:fs";
import path from "node:path";
import imageSigningKeys from "./image-signing-keys.json";

/**
 * The web app's side of tamper detection (module 12, task F6), the same check as the .NET
 * services' ReleaseIntegrity: the release manifest must carry Zelvo's cosign signature (ECDSA
 * P-256 over SHA-256, base64 DER, in `release-manifest.json.sig`), and the hash of the app's files
 * must be the one the release pipeline recorded for the `web` component. Release images (built
 * with ZELVO_RELEASE=true, compiled in as ZELVO_RELEASE_BUILD) refuse to start otherwise;
 * development builds only log.
 *
 * image-signing-keys.json is a copy of src/api/Zelvo.Licensing/image-signing-keys.json (a test
 * keeps them the same). It holds public keys only.
 */

export type ReleaseComponent = {
  name: string;
  image: string;
  digest: string;
  imageId?: string;
  filesSha256?: string;
  appDirectory?: string;
  exclude?: string[];
};

export type ReleaseManifest = { format: string; version: string; createdAt: string; components: ReleaseComponent[] };

export type ReleaseCheck = {
  status: "Verified" | "NoManifest" | "ManifestInvalid" | "ComponentUnknown" | "Modified";
  version?: string;
  reason: string;
};

type Key = { id: string; publicKey: string };

const keys: Key[] = imageSigningKeys as Key[];

/**
 * SHA-256 over the lines `"<sha256>  ./<path>\n"` of every file, sorted by the paths' UTF-8 bytes:
 * the same as `find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum`.
 */
export function hashDirectory(directory: string, exclude: string[] = []): string {
  const skipped = exclude.map((e) => e.replace(/^\/+|\/+$/g, "")).filter((e) => e.length > 0);
  const files: string[] = [];
  const walk = (relative: string) => {
    for (const entry of readdirSync(path.join(directory, relative), { withFileTypes: true })) {
      const child = relative ? `${relative}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        walk(child);
      } else if (entry.isFile() || (entry.isSymbolicLink() && statSync(path.join(directory, child)).isFile())) {
        files.push(`./${child}`);
      }
    }
  };
  walk("");
  const kept = files
    .filter((f) => !skipped.some((s) => f === `./${s}` || f.startsWith(`./${s}/`)))
    .sort((a, b) => Buffer.compare(Buffer.from(a, "utf8"), Buffer.from(b, "utf8")));
  const total = createHash("sha256");
  for (const file of kept) {
    const hash = createHash("sha256").update(readFileSync(path.join(directory, file.slice(2)))).digest("hex");
    total.update(`${hash}  ${file}\n`, "utf8");
  }
  return total.digest("hex");
}

/** Whether `signature` (base64 DER) signs `content` with one of `publicKeys`. */
export function verifySignature(content: Buffer, signature: string, publicKeys: Key[] = keys): boolean {
  let bytes: Buffer;
  try {
    bytes = Buffer.from(signature.trim(), "base64");
  } catch {
    return false;
  }
  return publicKeys.some((key) => {
    try {
      return verify("sha256", content, { key: key.publicKey, dsaEncoding: "der" }, bytes);
    } catch {
      return false;
    }
  });
}

/** Checks `component` in `appDirectory` against the manifest at `manifestPath`. */
export function checkRelease(component: string, manifestPath: string, appDirectory: string, publicKeys: Key[] = keys): ReleaseCheck {
  if (!existsSync(manifestPath)) {
    return { status: "NoManifest", reason: `No release manifest at ${manifestPath}.` };
  }
  if (!existsSync(`${manifestPath}.sig`)) {
    return { status: "ManifestInvalid", reason: "The release manifest has no signature." };
  }
  const content = readFileSync(manifestPath);
  if (!verifySignature(content, readFileSync(`${manifestPath}.sig`, "utf8"), publicKeys)) {
    return { status: "ManifestInvalid", reason: "The release manifest is not signed with Zelvo's image-signing key." };
  }
  let manifest: ReleaseManifest;
  try {
    manifest = JSON.parse(content.toString("utf8")) as ReleaseManifest;
  } catch {
    return { status: "ManifestInvalid", reason: "The release manifest cannot be read." };
  }
  if (manifest.format !== "zelvo-release/1") {
    return { status: "ManifestInvalid", reason: "The release manifest is not in the zelvo-release/1 format." };
  }
  const entry = manifest.components.find((c) => c.name === component);
  if (!entry?.filesSha256) {
    return { status: "ComponentUnknown", version: manifest.version, reason: `Release ${manifest.version} does not list '${component}'.` };
  }
  const actual = hashDirectory(appDirectory, entry.exclude ?? []);
  return actual === entry.filesSha256.toLowerCase()
    ? { status: "Verified", version: manifest.version, reason: `The files of '${component}' are those of release ${manifest.version}.` }
    : { status: "Modified", version: manifest.version, reason: `The files of '${component}' differ from release ${manifest.version}: the component was modified.` };
}

/** Runs the check when the server starts; a release image stops on failure. */
export function enforceReleaseIntegrity(): void {
  const manifestPath = process.env.RELEASE_MANIFEST_PATH ?? "/etc/zelvo/release/release-manifest.json";
  const result = checkRelease("web", manifestPath, process.cwd());
  if (result.status === "Verified") {
    console.info(`Release integrity verified: web is release ${result.version}`);
    return;
  }
  if (process.env.ZELVO_RELEASE_BUILD === "true") {
    console.error(`Release integrity check of web failed (${result.status}); the service refuses to start: ${result.reason}`);
    process.exit(1);
  }
  if (result.status !== "NoManifest") {
    console.warn(`Release integrity check of web failed (${result.status}): ${result.reason} Development builds start anyway.`);
  }
}

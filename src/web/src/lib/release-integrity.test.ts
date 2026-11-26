// @vitest-environment node
import { execFileSync } from "node:child_process";
import { generateKeyPairSync, sign } from "node:crypto";
import { appendFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { checkRelease, hashDirectory } from "./release-integrity";

describe("release integrity (task F6)", () => {
  let folder: string;
  let app: string;
  let manifestPath: string;
  const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  const keys = [{ id: "test", publicKey: publicKey.export({ type: "spki", format: "pem" }).toString() }];

  beforeEach(() => {
    folder = mkdtempSync(path.join(tmpdir(), "zelvo-web-release-"));
    app = path.join(folder, "app");
    mkdirSync(path.join(app, ".next", "cache"), { recursive: true });
    mkdirSync(path.join(app, "public", "fonts"), { recursive: true });
    writeFileSync(path.join(app, "server.js"), "server");
    writeFileSync(path.join(app, "public", "fonts", "زيلفو.woff2"), "font");
    writeFileSync(path.join(app, "public", "a b.txt"), "space");
    writeFileSync(path.join(app, ".next", "cache", "page"), "written while running");
    manifestPath = path.join(folder, "release-manifest.json");
  });

  afterEach(() => rmSync(folder, { recursive: true, force: true }));

  const writeManifest = (filesSha256: string, key = privateKey) => {
    const content = Buffer.from(
      JSON.stringify({
        format: "zelvo-release/1",
        version: "1.4.0",
        createdAt: new Date().toISOString(),
        components: [{ name: "web", image: "registry.example/zelvo/web", digest: `sha256:${"a".repeat(64)}`, filesSha256, exclude: [".next/cache"] }],
      }),
    );
    writeFileSync(manifestPath, content);
    writeFileSync(`${manifestPath}.sig`, sign("sha256", content, { key, dsaEncoding: "der" }).toString("base64"));
  };

  it("uses the image-signing keys of the .NET services", () => {
    const here = path.dirname(new URL(import.meta.url).pathname);
    expect(readFileSync(path.join(here, "image-signing-keys.json"), "utf8")).toBe(
      readFileSync(path.join(here, "..", "..", "..", "api", "Zelvo.Licensing", "image-signing-keys.json"), "utf8"),
    );
  });

  it("hashes files like the release pipeline's sha256sum", () => {
    const shell = execFileSync("sh", ["-c", "find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum"], { cwd: app }).toString();
    expect(hashDirectory(app)).toBe(shell.split(" ")[0]);
  });

  it("verifies matching files and notices changed ones", () => {
    writeManifest(hashDirectory(app, [".next/cache"]));
    writeFileSync(path.join(app, ".next", "cache", "page"), "changed while running");
    expect(checkRelease("web", manifestPath, app, keys).status).toBe("Verified");

    appendFileSync(path.join(app, "server.js"), "patched");
    expect(checkRelease("web", manifestPath, app, keys).status).toBe("Modified");
  });

  it("refuses manifests that are missing, changed or signed with another key", () => {
    expect(checkRelease("web", manifestPath, app, keys).status).toBe("NoManifest");

    const other = generateKeyPairSync("ec", { namedCurve: "prime256v1" }).privateKey;
    writeManifest(hashDirectory(app, [".next/cache"]), other);
    expect(checkRelease("web", manifestPath, app, keys).status).toBe("ManifestInvalid");

    writeManifest(hashDirectory(app, [".next/cache"]));
    writeFileSync(manifestPath, readFileSync(manifestPath, "utf8").replace("1.4.0", "1.5.0"));
    expect(checkRelease("web", manifestPath, app, keys).status).toBe("ManifestInvalid");

    writeManifest(hashDirectory(app, [".next/cache"]));
    expect(checkRelease("worker", manifestPath, app, keys).status).toBe("ComponentUnknown");
  });
});

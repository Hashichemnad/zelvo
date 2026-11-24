import { readFileSync } from "node:fs";
import { join } from "node:path";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import { permissionArea } from "./directory";

/**
 * Every permission of the API's catalog has a label on the Roles screen and in Settings →
 * Integrations (scopes), in English and Arabic, and its area has a name. Reads the keys from the
 * C# catalog so a new permission without labels fails here instead of showing MISSING_MESSAGE.
 */
function catalogPermissions(): string[] {
  const root = join(__dirname, "../../../api");
  const sources = [
    readFileSync(join(root, "Zelvo.Api/Identity/PermissionCatalog.cs"), "utf8"),
    readFileSync(join(root, "Zelvo.Tickets/IncidentSearch.cs"), "utf8"),
  ].join("\n");
  const keys = [...sources.matchAll(/const string \w+ = "([a-z]+\.[a-z]+)";/g)].map((m) => m[1]!);
  return [...new Set(keys)].sort();
}

describe("permission labels", () => {
  const permissions = catalogPermissions();

  it("reads the whole catalog", () => {
    expect(permissions).toEqual(expect.arrayContaining(["incidents.read", "knowledge.publish", "ai.manage", "sla.manage"]));
  });

  for (const [locale, messages] of [["en", en], ["ar", ar]] as const) {
    it(`labels every permission and area in ${locale}`, () => {
      const roles = messages.directory.permissions as Record<string, unknown>;
      const areas = roles.areas as Record<string, string>;
      const scopes = messages.settings.integrations.permissions as Record<string, string>;
      const key = (p: string) => p.replaceAll(".", "_");
      expect(permissions.filter((p) => typeof roles[key(p)] !== "string")).toEqual([]);
      expect(permissions.filter((p) => typeof scopes[key(p)] !== "string")).toEqual([]);
      expect([...new Set(permissions.map(permissionArea))].filter((a) => typeof areas[a] !== "string")).toEqual([]);
    });
  }
});

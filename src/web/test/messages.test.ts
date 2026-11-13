import ar from "../messages/ar.json";
import en from "../messages/en.json";

type Tree = { [key: string]: string | Tree };

function flatten(tree: Tree, prefix = ""): Record<string, string> {
  return Object.entries(tree).reduce<Record<string, string>>((acc, [key, value]) => {
    const path = prefix ? `${prefix}.${key}` : key;
    return typeof value === "string" ? { ...acc, [path]: value } : { ...acc, ...flatten(value, path) };
  }, {});
}

const english = flatten(en);
const arabic = flatten(ar);

describe("translation files", () => {
  it("have the same keys in English and Arabic", () => {
    expect(Object.keys(arabic).sort()).toEqual(Object.keys(english).sort());
  });

  it("have no empty values", () => {
    for (const [key, value] of Object.entries({ ...english, ...arabic })) {
      expect(value.trim(), key).not.toBe("");
    }
  });

  it("use Arabic script for Arabic text", () => {
    // Every Arabic value contains Arabic letters, except the language switch target, which is
    // deliberately the other language's own name.
    const exceptions = new Set(["language.switchTo"]);
    for (const [key, value] of Object.entries(arabic)) {
      if (!exceptions.has(key)) {
        expect(value, key).toMatch(/[؀-ۿ]/);
      }
    }
  });
});

import { readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

/**
 * Layouts must work left-to-right and right-to-left, so physical direction utilities are not
 * allowed; use logical ones (ms-/me-, ps-/pe-, start-/end-, text-start/text-end, border-s/border-e).
 */
const physical =
  /(?<![\w-])(?:-?m[lr]|p[lr]|-?left|-?right|text-left|text-right|float-left|float-right|border-[lr]|rounded-[lr]|rounded-[tb][lr]|scroll-[mp][lr])(?:-[\w./[\]]+)?(?![\w-])/;

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return sourceFiles(full);
    return /\.(tsx|ts|css)$/.test(name) && !name.endsWith(".test.ts") && !name.endsWith(".test.tsx") ? [full] : [];
  });
}

describe("RTL guard", () => {
  it("uses logical instead of physical direction classes", () => {
    const offenders: string[] = [];
    for (const file of sourceFiles(path.resolve(__dirname, "../src"))) {
      const lines = readFileSync(file, "utf8").split("\n");
      lines.forEach((line, index) => {
        for (const className of line.match(/className="([^"]*)"/g) ?? []) {
          const match = className.match(physical);
          if (match) offenders.push(`${path.relative(process.cwd(), file)}:${index + 1} ${match[0]}`);
        }
        if (/\b(?:margin|padding)-(?:left|right)\b|(?<![\w-])(?:left|right)\s*:/.test(line) && file.endsWith(".css")) {
          offenders.push(`${path.relative(process.cwd(), file)}:${index + 1}`);
        }
      });
    }
    expect(offenders).toEqual([]);
  });
});

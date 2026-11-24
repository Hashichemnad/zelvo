import { readFileSync } from "node:fs";
import path from "node:path";
import { checkAnswers } from "./check";
import { normalizeFormat } from "./formats";
import { evaluate, order } from "./rules";
import type { Answers, FieldFormat, FormField } from "./types";

/** Cases shared with the server (Zelvo.Api.Tests.FormEngineTests), so both check the same way. */
const cases = JSON.parse(readFileSync(path.resolve(__dirname, "../../../../../tests/shared/form-cases.json"), "utf8")) as {
  formats: { format: FieldFormat; input: string; expected: string | null }[];
  rules: { name: string; fields: FormField[]; cases: { answers: Answers; expected: Record<string, [boolean, boolean, boolean]> }[] }[];
};

describe("form formats", () => {
  it.each(cases.formats.map((c) => [c.format, c.input, c.expected] as const))("%s %j → %j", (format, input, expected) => {
    expect(normalizeFormat(format, input)).toBe(expected);
  });
});

describe("form rules", () => {
  for (const group of cases.rules) {
    it(group.name, () => {
      for (const c of group.cases) {
        const states = evaluate(group.fields, c.answers);
        for (const [key, [visible, required, readOnly]] of Object.entries(c.expected)) {
          expect({ key, answers: c.answers, ...states[key] }).toEqual({ key, answers: c.answers, visible, required, readOnly });
        }
      }
    });
  }

  it("finds rule cycles", () => {
    const show = (other: string) => [{ action: "Show" as const, match: "All" as const, conditions: [{ field: other, operator: "NotEmpty" as const }] }];
    const fields: FormField[] = [
      { key: "a", type: "Text", label: { en: "A" }, rules: show("b") },
      { key: "b", type: "Text", label: { en: "B" }, rules: show("a") },
    ];
    expect(order(fields)).toBeNull();
  });
});

describe("answer checks", () => {
  const fields: FormField[] = [
    { key: "addMonitor", type: "Checkbox", label: { en: "Add a monitor" } },
    {
      key: "size",
      type: "Dropdown",
      label: { en: "Size" },
      options: [{ value: "24", label: { en: "24" } }],
      required: true,
      rules: [{ action: "Show", match: "All", conditions: [{ field: "addMonitor", operator: "Checked" }] }],
    },
    { key: "iban", type: "Text", label: { en: "IBAN" }, validation: { format: "Iban" } },
    { key: "dept", type: "Text", label: { en: "Department" }, readOnly: true, profileDefault: "Department" },
  ];

  it("leaves out hidden answers, keeps read-only defaults and finds problems", () => {
    const ok = checkAnswers(fields, { size: "24", dept: "Other", addMonitor: false }, { dept: "Finance" });
    expect(ok.errors).toEqual([]);
    expect(ok.toSend).toEqual({ addMonitor: false });

    const bad = checkAnswers(fields, { addMonitor: true, iban: "AE07 0331 2345 6789 0123 457" }, {});
    expect(bad.errors.map((e) => `${e.field}:${e.code}`).sort()).toEqual(["iban:format-iban", "size:required"]);
  });
});

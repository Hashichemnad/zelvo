import type { Answers, FormField, JsonValue, RuleCondition, RuleAction } from "./types";

/**
 * Field rules (module 2: show, hide, require or make read-only based on other answers), the same as
 * the server's (Zelvo.Tickets.Forms.FormRules; shared cases in tests/shared/form-cases.json). The
 * browser evaluates them as people type; the server always evaluates them again.
 */

export interface FieldState {
  visible: boolean;
  required: boolean;
  readOnly: boolean;
}

export function isEmpty(value: JsonValue | undefined): boolean {
  if (value === null || value === undefined || value === false) return true;
  if (typeof value === "string") return value.trim().length === 0;
  if (Array.isArray(value)) return value.length === 0;
  if (typeof value === "object") return Object.keys(value).length === 0;
  return false;
}

function equalsValue(value: JsonValue, expected: string): boolean {
  if (Array.isArray(value)) return value.some((item) => item !== null && equalsValue(item, expected));
  if (typeof value === "number") {
    const n = Number(expected.trim());
    return expected.trim() !== "" && !Number.isNaN(n) && value === n;
  }
  if (typeof value === "boolean") return expected.toLowerCase() === String(value);
  if (typeof value === "string") return value.trim().toLowerCase() === expected.trim().toLowerCase();
  return false;
}

function containsValue(value: JsonValue, expected: string): boolean {
  if (Array.isArray(value)) return value.some((item) => item !== null && equalsValue(item, expected));
  return typeof value === "string" && value.toLowerCase().includes(expected.trim().toLowerCase());
}

const datePattern = /^\d{4}-\d{2}-\d{2}$/;

function isDate(value: string): boolean {
  if (!datePattern.test(value)) return false;
  const d = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === value;
}

function compare(value: JsonValue | undefined, expected: string): number | null {
  if (typeof value === "number") {
    const n = Number(expected.trim());
    return expected.trim() === "" || Number.isNaN(n) ? null : Math.sign(value - n);
  }
  if (typeof value === "string" && isDate(value) && isDate(expected)) {
    return value < expected ? -1 : value > expected ? 1 : 0;
  }
  return null;
}

/** Whether a condition holds for an answer (undefined when the field is hidden or not answered). */
export function test(condition: RuleCondition, value: JsonValue | undefined): boolean {
  const expected = condition.value ?? "";
  switch (condition.operator) {
    case "Empty":
      return isEmpty(value);
    case "NotEmpty":
      return !isEmpty(value);
    case "Checked":
      return value === true;
    case "NotChecked":
      return value !== true;
    case "Equals":
      return !isEmpty(value) && equalsValue(value!, expected);
    case "NotEquals":
      return isEmpty(value) || !equalsValue(value!, expected);
    case "Contains":
      return !isEmpty(value) && containsValue(value!, expected);
    case "GreaterThan":
      return (compare(value, expected) ?? 0) > 0;
    case "LessThan":
      return (compare(value, expected) ?? 0) < 0;
    default:
      return false;
  }
}

export function dependencies(field: FormField): string[] {
  return [...new Set((field.rules ?? []).flatMap((r) => r.conditions.map((c) => c.field)))];
}

/** Fields ordered so each comes after the fields its rules look at; null when the rules form a cycle. */
export function order(fields: FormField[]): FormField[] | null {
  const byKey = new Map<string, FormField>();
  for (const f of fields) if (!byKey.has(f.key)) byKey.set(f.key, f);
  const state = new Map<string, 1 | 2>();
  const ordered: FormField[] = [];
  const visit = (field: FormField): boolean => {
    const s = state.get(field.key);
    if (s) return s === 2;
    state.set(field.key, 1);
    for (const dep of dependencies(field)) {
      const other = byKey.get(dep);
      if (other && !visit(other)) return false;
    }
    state.set(field.key, 2);
    ordered.push(field);
    return true;
  };
  for (const field of byKey.values()) if (!visit(field)) return null;
  return ordered;
}

/** The state of every field for the answers. */
export function evaluate(fields: FormField[], answers: Answers): Record<string, FieldState> {
  const keys = new Set(fields.map((f) => f.key));
  const states: Record<string, FieldState> = {};
  for (const field of order(fields) ?? fields) {
    const rules = field.rules ?? [];
    const matches = (action: RuleAction) =>
      rules
        .filter((r) => r.action === action)
        .map((rule) =>
          rule.conditions.length > 0 &&
          (rule.match === "All" ? rule.conditions.every(holds) : rule.conditions.some(holds)),
        );
    function holds(c: RuleCondition): boolean {
      const visible = !keys.has(c.field) || states[c.field]?.visible === true;
      return test(c, visible ? answers[c.field] : undefined);
    }
    const shows = matches("Show");
    const visible = (shows.length === 0 || shows.some(Boolean)) && !matches("Hide").some(Boolean);
    const required = visible && (!!field.required || matches("Require").some(Boolean));
    const readOnly = !!field.readOnly || matches("ReadOnly").some(Boolean);
    states[field.key] = { visible, required, readOnly };
  }
  return states;
}

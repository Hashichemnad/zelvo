import { normalizeFormat } from "./formats";
import { evaluate, isEmpty, type FieldState } from "./rules";
import type { AnswerError, Answers, FormField, JsonValue } from "./types";

/**
 * Checks answers in the browser before they are sent, so people see problems at once. The server
 * (Zelvo.Tickets.Forms.FormAnswers) checks everything again, including what answers refer to
 * (users, locations, uploaded files), and its errors use the same codes.
 */

export function maxTextLength(field: FormField): number {
  return field.type === "RichText" ? 20000 : field.multiline ? 10000 : 2000;
}

const formatCodes: Record<string, string> = {
  EmiratesId: "format-emirates-id",
  Phone: "format-phone",
  Email: "format-email",
  Iban: "format-iban",
};

const datePattern = /^\d{4}-\d{2}-\d{2}$/;

function checkValue(field: FormField, value: JsonValue, path: string, errors: AnswerError[]): void {
  const v = field.validation ?? {};
  const add = (code: string): void => {
    errors.push({ field: path, code });
  };
  switch (field.type) {
    case "Text":
    case "RichText": {
      if (typeof value !== "string") return add("type");
      const text = value.trim();
      const max = Math.min(v.maxLength ?? Infinity, maxTextLength(field));
      if (text.length > max) return add("max-length");
      if (v.minLength != null && text.length < v.minLength) return add("min-length");
      if (v.format && normalizeFormat(v.format, text) === null) return add(formatCodes[v.format] ?? "pattern");
      if (v.pattern) {
        let matches = false;
        try {
          matches = new RegExp(`^(?:${v.pattern})$`, "u").test(v.format ? normalizeFormat(v.format, text)! : text);
        } catch {
          // The server decides on patterns it accepts; this check only helps.
          matches = true;
        }
        if (!matches) return add("pattern");
      }
      return;
    }
    case "Number": {
      const n = typeof value === "number" ? value : typeof value === "string" && value.trim() !== "" ? Number(value) : NaN;
      if (Number.isNaN(n)) return add("type");
      if (v.min != null && n < v.min) return add("min");
      if (v.max != null && n > v.max) return add("max");
      return;
    }
    case "Date": {
      if (typeof value !== "string" || !datePattern.test(value)) return add("type");
      if (v.minDate && value < v.minDate) return add("min-date");
      if (v.maxDate && value > v.maxDate) return add("max-date");
      return;
    }
    case "Dropdown":
    case "Radio": {
      const options = new Set((field.options ?? []).map((o) => o.value));
      const chosen = Array.isArray(value) ? value : [value];
      if (chosen.some((c) => typeof c !== "string" || !options.has(c))) return add("option-unknown");
      return;
    }
    case "File": {
      if (!Array.isArray(value)) return add("type");
      if (value.length > (v.maxFiles ?? 5)) return add("max-files");
      return;
    }
    case "Signature": {
      if (typeof value !== "object" || value === null || Array.isArray(value)) return add("type");
      if ("attachmentId" in value) return;
      const name = typeof value.typedName === "string" ? value.typedName.trim() : "";
      if (!name || value.confirmed !== true) return add("signature-incomplete");
      return;
    }
    case "Table": {
      if (!Array.isArray(value)) return add("type");
      const max = Math.min(field.maxRows ?? 100, 100);
      if (value.length > max) return add("max-rows");
      if (field.minRows != null && value.length < field.minRows) return add("min-rows");
      value.forEach((row, i) => {
        if (typeof row !== "object" || row === null || Array.isArray(row)) return;
        for (const column of field.columns ?? []) {
          const cell = row[column.key];
          if (isEmpty(cell)) {
            if (column.required) errors.push({ field: `${path}.${i}.${column.key}`, code: "required" });
          } else {
            checkValue(column, cell!, `${path}.${i}.${column.key}`, errors);
          }
        }
      });
      return;
    }
    default:
      return;
  }
}

/** The answers as the form shows them: what was given, else the default; read-only fields keep the default. */
export function effectiveAnswers(fields: FormField[], answers: Answers, defaults: Answers): Answers {
  const working: Answers = {};
  for (const field of fields) {
    working[field.key] = (field.key in answers ? answers[field.key] : defaults[field.key]) ?? null;
  }
  const states = evaluate(fields, working);
  for (const field of fields) {
    if (states[field.key]?.readOnly) working[field.key] = defaults[field.key] ?? null;
  }
  return working;
}

/**
 * Checks answers like the server does; returns the errors, the field states and the answers to send
 * (hidden fields left out, read-only fields as their default).
 */
export function checkAnswers(
  fields: FormField[],
  answers: Answers,
  defaults: Answers,
): { errors: AnswerError[]; states: Record<string, FieldState>; toSend: Answers } {
  const working = effectiveAnswers(fields, answers, defaults);
  const states = evaluate(fields, working);
  const errors: AnswerError[] = [];
  const toSend: Answers = {};
  for (const field of fields) {
    const state = states[field.key];
    const value = working[field.key] ?? null;
    if (!state?.visible) continue;
    if (isEmpty(value)) {
      if (state.required) errors.push({ field: field.key, code: "required" });
      else if (field.type === "Checkbox" && value === false) toSend[field.key] = false;
      continue;
    }
    checkValue(field, value, field.key, errors);
    if (!state.readOnly) toSend[field.key] = value;
  }
  return { errors, states, toSend };
}

/** The errors of one field (and, for tables, its cells). */
export function errorsFor(errors: AnswerError[], key: string): AnswerError[] {
  return errors.filter((e) => e.field === key || e.field.startsWith(`${key}.`));
}

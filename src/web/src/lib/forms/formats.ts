import type { FieldFormat } from "./types";

/**
 * Format checks for form answers (module 2), the same as the server's
 * (Zelvo.Tickets.Forms.FieldFormats; shared cases in tests/shared/form-cases.json). Each returns
 * the value in its normal form, or null when it is not valid. The server checks again.
 */

const ibanLengths: Record<string, number> = {
  AE: 23, SA: 24, QA: 29, KW: 30, BH: 22, OM: 23, JO: 30, EG: 29, LB: 28, IQ: 23,
  PK: 24, TR: 26, GB: 22, DE: 22, FR: 27, NL: 18, CH: 21, IE: 22, ES: 24, IT: 27,
};

export function normalizeFormat(format: FieldFormat, value: string): string | null {
  switch (format) {
    case "EmiratesId":
      return emiratesId(value);
    case "Phone":
      return phone(value);
    case "Email":
      return email(value);
    case "Iban":
      return iban(value);
    default:
      return null;
  }
}

/** ASCII digits (Arabic-Indic and Persian digits converted), skipping separators; null when anything else is there. */
function digits(value: string, separators: string): string | null {
  let out = "";
  for (const c of value.trim()) {
    if (separators.includes(c)) continue;
    const code = c.charCodeAt(0);
    if (c >= "0" && c <= "9") out += c;
    else if (code >= 0x0660 && code <= 0x0669) out += String(code - 0x0660);
    else if (code >= 0x06f0 && code <= 0x06f9) out += String(code - 0x06f0);
    else return null;
  }
  return out;
}

function luhn(value: string): boolean {
  let sum = 0;
  for (let i = 0; i < value.length; i++) {
    let d = Number(value[value.length - 1 - i]);
    if (i % 2 === 1) {
      d *= 2;
      if (d > 9) d -= 9;
    }
    sum += d;
  }
  return sum % 10 === 0;
}

export function emiratesId(value: string): string | null {
  const d = digits(value, "- ");
  return d && d.length === 15 && d.startsWith("784") && luhn(d) ? d : null;
}

export function phone(value: string): string | null {
  const trimmed = value.trim();
  const plus = trimmed.startsWith("+");
  const d = digits(plus ? trimmed.slice(1) : trimmed, " -.()");
  if (!d) return null;
  let international: string;
  if (plus) international = d;
  else if (d.startsWith("00")) international = d.slice(2);
  else if (d.startsWith("0") && (d.length === 9 || d.length === 10)) international = "971" + d.slice(1);
  else return null;
  if (international.length < 7 || international.length > 15 || international[0] === "0") return null;
  // UAE: mobile numbers (5x) have 9 digits after 971, landlines 8.
  if (international.startsWith("971") && international.length !== (international[3] === "5" ? 12 : 11)) return null;
  return "+" + international;
}

export function email(value: string): string | null {
  const trimmed = value.trim();
  if (trimmed.length === 0 || trimmed.length > 254) return null;
  const at = trimmed.lastIndexOf("@");
  if (at <= 0 || at === trimmed.length - 1) return null;
  const local = trimmed.slice(0, at);
  const domain = trimmed.slice(at + 1);
  if (/[\s<>()[\]\\,;:"]/.test(local) || !/^[A-Za-z0-9.-]+$/.test(domain) || !domain.includes(".")
    || domain.startsWith(".") || domain.endsWith(".") || domain.includes("..") || local.startsWith(".") || local.endsWith(".") || local.includes("..")) {
    return null;
  }
  return `${local}@${domain.toLowerCase()}`;
}

export function iban(value: string): string | null {
  let compact = "";
  for (const c of value) {
    if (c === " " || c === "-") continue;
    if (!/^[A-Za-z0-9]$/.test(c)) return null;
    compact += c.toUpperCase();
  }
  if (compact.length < 15 || compact.length > 34 || !/^[A-Z]{2}[0-9]{2}/.test(compact)) return null;
  const length = ibanLengths[compact.slice(0, 2)];
  if (length !== undefined && compact.length !== length) return null;
  let remainder = 0;
  for (const c of compact.slice(4) + compact.slice(0, 4)) {
    const n = c >= "0" && c <= "9" ? c.charCodeAt(0) - 48 : c.charCodeAt(0) - 55;
    remainder = n >= 10 ? (remainder * 100 + n) % 97 : (remainder * 10 + n) % 97;
  }
  return remainder === 1 ? compact : null;
}

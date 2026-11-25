/**
 * Online payments (task E8, module 15) as the API returns them. See docs/architecture/payments.md.
 */
import type { Bilingual, Person } from "./incidents";

export type PaymentTiming = "OnSubmission" | "AfterApproval" | "OnCompletion";
export type PaymentState = "AwaitingPayment" | "Paid" | "Expired" | "Cancelled" | "PartlyRefunded" | "Refunded";
export type GatewayMode = "Test" | "Live";
export type BodyFormat = "None" | "Json" | "Form";
export type SignatureAlgorithm = "None" | "HmacSha256" | "HmacSha512";

export const paymentTimings: PaymentTiming[] = ["OnSubmission", "AfterApproval", "OnCompletion"];

export interface FeeLine {
  name: Bilingual;
  amountAed: number;
}

export interface Payment {
  id: string;
  request: { id: string; number: string; title: string } | null;
  service: { id: string; code: string; name: Bilingual } | null;
  payer: Person | null;
  amountAed: number;
  refundedAed: number;
  currency: string;
  lines: FeeLine[];
  timing: PaymentTiming;
  state: PaymentState;
  gateway: string | null;
  receiptNumber: string | null;
  transactionId: string | null;
  expiresAt: string | null;
  paidAt: string | null;
  createdAt: string;
  attempts: { reference: string; state: "Redirected" | "Paid" | "Failed" | "Cancelled" | "Error"; gatewayStatus: string | null; createdAt: string; endedAt: string | null }[];
  refunds: { id: string; amountAed: number; reason: string; state: "Completed" | "Failed" | "Manual"; createdAt: string }[];
}

export interface Receipt {
  receiptNumber: string;
  organisation: Bilingual;
  request: { id: string; number: string; title: string } | null;
  service: { id: string; code: string; name: Bilingual } | null;
  payer: Person | null;
  amountAed: number;
  refundedAed: number;
  currency: string;
  lines: FeeLine[];
  transactionId: string | null;
  paidAt: string;
  gateway: string | null;
}

export interface SignatureSettings {
  algorithm: SignatureAlgorithm;
  secretName: string | null;
  header: string | null;
  encoding: "Hex" | "Base64";
  prefix: string | null;
}

export interface GatewayOperation {
  method: string;
  url: string;
  bodyFormat: BodyFormat;
  headers: Record<string, string>;
  body: Record<string, string>;
  response: Record<string, string>;
  signature: SignatureSettings;
}

export interface GatewayConnector {
  create: GatewayOperation;
  status: GatewayOperation;
  refund: GatewayOperation | null;
  statusValues: { paid: string[]; failed: string[]; cancelled: string[] };
  refundSucceeded: string[];
  statusAmountInMinorUnits: boolean;
  callback: { referenceField: string; signature: SignatureSettings };
  settlement: { reference: string; transactionId: string | null; amount: string; status: string | null; amountInMinorUnits: boolean } | null;
}

export interface Gateway {
  id: string;
  name: string;
  merchantId: string | null;
  mode: GatewayMode;
  enabled: boolean;
  isDefault: boolean;
  connector: GatewayConnector;
  credentialNames: string[];
  testPaymentSucceededAt: string | null;
  updatedAt: string;
}

export interface Reconciliation {
  id: string;
  gatewayId: string;
  gateway: string | null;
  day: string;
  matched: number;
  mismatched: number;
  unknownToZelvo: number;
  totalAed: number;
  fileImportedAt: string | null;
  updatedAt: string;
  lines: { result: string; reference: string | null; receipt: string | null; transactionId: string | null; zelvoAmountAed: number | null; gatewayAmountAed: number | null; zelvoState: string | null; gatewayStatus: string | null }[] | null;
}

const noSignature = (): SignatureSettings => ({ algorithm: "None", secretName: null, header: null, encoding: "Hex", prefix: null });

export function emptyOperation(method: string, bodyFormat: BodyFormat): GatewayOperation {
  return { method, url: "", bodyFormat, headers: {}, body: {}, response: {}, signature: noSignature() };
}

/** A new gateway's connector: the fields Zelvo needs, to be mapped from the gateway's documentation. */
export function emptyConnector(): GatewayConnector {
  return {
    create: { ...emptyOperation("POST", "Json"), response: { transactionId: "", redirectUrl: "" } },
    status: { ...emptyOperation("GET", "None"), response: { status: "", amount: "", reference: "" } },
    refund: null,
    statusValues: { paid: [], failed: [], cancelled: [] },
    refundSucceeded: [],
    statusAmountInMinorUnits: false,
    callback: { referenceField: "reference", signature: noSignature() },
    settlement: null,
  };
}

/** "name = value" lines ⇄ a map, for headers, body fields and answer paths. */
export function linesOf(map: Record<string, string>): string {
  return Object.entries(map).map(([k, v]) => `${k} = ${v}`).join("\n");
}

export function mapOf(lines: string): Record<string, string> {
  const map: Record<string, string> = {};
  for (const line of lines.split("\n")) {
    const at = line.indexOf("=");
    if (at <= 0) continue;
    const key = line.slice(0, at).trim();
    if (key) map[key] = line.slice(at + 1).trim();
  }
  return map;
}

/** Comma-separated values ⇄ a list. */
export const listOf = (text: string): string[] => text.split(",").map((v) => v.trim()).filter(Boolean);

/** An amount in AED, in the person's digits. */
export function formatAed(amount: number, locale: string, digits: "latn" | "arab"): string {
  return new Intl.NumberFormat(`${locale}-AE-u-nu-${digits}`, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount);
}

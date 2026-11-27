/**
 * Asset management (task G3, module 18) as the API returns it: assets and their lifecycle,
 * contracts and warranties, software licences with compliance, discovered software and the
 * assets assigned to a person. The server is Zelvo.Api/Assets; keep the two equal.
 */
import type { Bilingual } from "./incidents";
import type { CiFamily, CiStatus } from "./cmdb";

export type AssetState = "Requested" | "Ordered" | "Received" | "InStock" | "Assigned" | "InRepair" | "Retired" | "Disposed";
export const assetStates: readonly AssetState[] = ["Requested", "Ordered", "Received", "InStock", "Assigned", "InRepair", "Retired", "Disposed"];

export type AssetContractKind = "Warranty" | "Support" | "Maintenance" | "Lease";
export const contractKinds: readonly AssetContractKind[] = ["Warranty", "Support", "Maintenance", "Lease"];

export type LicenseMetric = "Device" | "User";

export type LabelLayout = "Small" | "Large";

/** Families whose items can be assets. */
export const assetFamilies: readonly CiFamily[] = ["Hardware", "Software"];

export interface AssetPerson {
  id: string;
  displayName: string | null;
}

export interface AssetClassRef {
  id: string;
  key: string;
  family: CiFamily;
  path: Bilingual;
}

export interface AssetSummary {
  id: string;
  tag: string;
  number: string;
  name: string;
  class: AssetClassRef | null;
  state: AssetState;
  itemStatus: CiStatus;
  assignedTo: AssetPerson | null;
  warrantyEndsOn: string | null;
}

export interface AssetPage {
  items: AssetSummary[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface AssetContractRef {
  id: string;
  kind: AssetContractKind;
  name: string;
  supplier: string | null;
  startsOn: string | null;
  endsOn: string;
}

export interface Asset {
  id: string;
  tag: string;
  number: string;
  name: string;
  class: AssetClassRef | null;
  state: AssetState;
  nextStates: AssetState[];
  itemStatus: CiStatus;
  assignedTo: AssetPerson | null;
  assignedAt: string | null;
  request: { id: string; number: string; title: string } | null;
  purchaseDate: string | null;
  purchaseCost: number | null;
  supplier: string | null;
  orderNumber: string | null;
  notes: string | null;
  contracts: AssetContractRef[];
  licenses: { id: string; name: string; product: string }[];
  createdAt: string;
  updatedAt: string;
  etag: string;
}

export interface AssetContract {
  id: string;
  kind: AssetContractKind;
  name: string;
  reference: string | null;
  supplier: string | null;
  startsOn: string | null;
  endsOn: string;
  costAed: number | null;
  notes: string | null;
  owner: AssetPerson | null;
  itemCount: number;
  items: { id: string; tag: string; number: string; name: string }[];
  etag: string;
}

export interface SoftwareLicense {
  id: string;
  name: string;
  publisher: string | null;
  products: { key: string; name: string | null }[];
  metric: LicenseMetric;
  quantity: number;
  startsOn: string | null;
  endsOn: string | null;
  supplier: string | null;
  reference: string | null;
  costAed: number | null;
  notes: string | null;
  owner: AssetPerson | null;
  devices: number;
  people: number;
  used: number;
  shortfall: number;
  spare: number;
  etag: string;
}

export interface SoftwareProduct {
  productKey: string;
  name: string;
  publisher: string | null;
  devices: number;
  licenses: string[];
}

export interface SoftwarePage {
  items: SoftwareProduct[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface PersonAsset {
  id: string;
  tag: string;
  number: string;
  name: string;
  classPath: Bilingual | null;
  state: AssetState;
  assignedAt: string | null;
  warrantyEndsOn: string | null;
  /** Tickets the item is on (staff only). */
  ticketCount: number | null;
}

export interface AssetSettings {
  tagPrefix: string;
  expiryWarningDays: number;
}

/** Days from today (UTC) to an ISO date, negative when it is past. */
export function daysUntil(isoDate: string, today: Date = new Date()): number {
  const [y = 1970, m = 1, d = 1] = isoDate.split("-").map(Number);
  const end = Date.UTC(y, m - 1, d);
  const start = Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate());
  return Math.round((end - start) / 86_400_000);
}

/** An ISO date (YYYY-MM-DD) in the person's language, calendar and digits, without a time. */
export function formatDay(isoDate: string, locale: string, calendar: "gregory" | "islamic-umalqura", digits: "latn" | "arab"): string {
  const [y = 1970, m = 1, d = 1] = isoDate.split("-").map(Number);
  return new Intl.DateTimeFormat(`${locale === "ar" ? "ar" : "en"}-AE-u-ca-${calendar}-nu-${digits}`, { day: "numeric", month: "long", year: "numeric", timeZone: "UTC" })
    .format(new Date(Date.UTC(y, m - 1, d)));
}

/** Asset tags as typed on labels: capital letters, digits and hyphens. */
export function normaliseTag(text: string): string {
  return text.trim().toUpperCase();
}

export const tagPattern = /^[A-Z0-9]([A-Z0-9-]{0,30}[A-Z0-9])?$/;

/**
 * The CMDB (task G1, module 18) as the API returns it: classes with attributes, configuration
 * items, relationship types and relationships, the relationship map, impact and the items on
 * tickets. The server is Zelvo.Api/Cmdb; keep the two equal.
 */
import type { Bilingual, Locale, TicketState } from "./incidents";
import { inLanguage } from "./incidents";
import type { FormField, JsonValue } from "./forms/types";

export type CiFamily = "Hardware" | "Software" | "BusinessService" | "Application" | "Network" | "CloudResource";
export const ciFamilies: readonly CiFamily[] = ["BusinessService", "Application", "Software", "Hardware", "Network", "CloudResource"];

export type CiStatus = "Planned" | "Active" | "Maintenance" | "Retired";
export const ciStatuses: readonly CiStatus[] = ["Planned", "Active", "Maintenance", "Retired"];

export type ImpactDirection = "ToSource" | "ToTarget" | "Both" | "None";
export const impactDirections: readonly ImpactDirection[] = ["ToSource", "ToTarget", "Both", "None"];

export type RelationshipTargetKind = "ConfigurationItem" | "Consumer";

export type CiNodeKind = "ConfigurationItem" | "User" | "Department" | "CustomerGroup";

/** Field types an attribute may have (the server's CmdbAttributes.AllowedTypes). */
export const attributeTypes = ["Text", "Number", "Date", "Dropdown", "Checkbox", "Radio", "User", "Location"] as const;

/** The deepest level of the class tree. */
export const maxClassDepth = 4;

export interface CiClassRef {
  id: string;
  key: string;
  family: CiFamily;
  name: Bilingual;
  path: Bilingual;
}

export interface InheritedAttribute {
  classId: string;
  className: Bilingual;
  field: FormField;
}

export interface CiClass {
  id: string;
  key: string;
  parentId: string | null;
  depth: number;
  family: CiFamily;
  name: Bilingual;
  description: Bilingual;
  path: Bilingual;
  attributes: FormField[] | null;
  inheritedAttributes: InheritedAttribute[];
  requesterVisible: boolean;
  active: boolean;
  selectable: boolean;
  itemCount: number;
  etag: string;
  /** Task G2: the class's own identification rules (null: inherited). */
  identificationRules: string[][] | null;
  /** The rules that apply to its items. */
  effectiveIdentificationRules: string[][];
}

export interface CiRelationshipType {
  id: string;
  key: string;
  outgoingName: Bilingual;
  incomingName: Bilingual;
  targetKind: RelationshipTargetKind;
  impact: ImpactDirection;
  builtIn: boolean;
  active: boolean;
  relationshipCount: number;
}

export interface CiRelationshipTypeRef {
  id: string;
  key: string;
  outgoingName: Bilingual;
  incomingName: Bilingual;
  impact: ImpactDirection;
}

export interface CmdbSettings {
  numberPrefix: string;
  /** Task G2: precedence of values people set, 1 (most trusted) to 100. */
  manualPriority: number;
}

interface NamedRef {
  id: string;
  name: Bilingual;
}

interface NodeRef extends NamedRef {
  path: Bilingual;
}

export interface CiSummary {
  id: string;
  number: string;
  name: string;
  class: CiClassRef;
  status: CiStatus;
  ownerGroup: NamedRef | null;
  location: NodeRef | null;
  updatedAt: string;
  /** Task G2: since when no discovery source reports it. */
  missingSince: string | null;
}

export interface CiPage {
  items: CiSummary[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface CiAttribute {
  field: FormField;
  classId: string;
  value: JsonValue | null;
  /** For users and locations: the name or path the value refers to. */
  display: Bilingual | null;
}

export interface ConfigurationItem {
  id: string;
  number: string;
  name: string;
  description: Bilingual;
  class: CiClassRef;
  status: CiStatus;
  ownerGroup: NamedRef | null;
  location: NodeRef | null;
  attributes: CiAttribute[];
  stale: Record<string, JsonValue>;
  relationshipCount: number;
  ticketCount: number;
  createdAt: string;
  updatedAt: string;
  etag: string;
  /** Task G2: since when no discovery source reports it. */
  missingSince: string | null;
  /** Task G2: the item it was merged into. */
  mergedIntoId: string | null;
}

/** An item to pick (lookup, tickets). */
export interface CiPick {
  id: string;
  number: string;
  name: string;
  status: CiStatus;
  class: CiClassRef;
}

/** An end of a relationship or a node of the map. */
export interface CiNode {
  kind: CiNodeKind;
  id: string;
  number: string | null;
  name: string | null;
  localizedName: Bilingual | null;
  class: CiClassRef | null;
  status: CiStatus | null;
}

export interface CiRelationship {
  id: string;
  direction: "Outgoing" | "Incoming";
  type: CiRelationshipTypeRef;
  /** The type's name in this direction. */
  name: Bilingual;
  other: CiNode;
  note: string | null;
  createdAt: string;
}

export interface CiImpactEntry {
  node: CiNode;
  depth: number;
  via: CiNode;
  relationship: CiRelationshipTypeRef;
}

export interface CiImpact {
  items: CiImpactEntry[];
  businessServices: CiImpactEntry[];
  consumers: CiImpactEntry[];
  userCount: number;
  truncated: boolean;
}

export interface CiMapNode {
  node: CiNode;
  depth: number;
}

export interface CiMapEdge {
  id: string;
  source: string;
  target: string;
  type: CiRelationshipTypeRef;
  note: string | null;
}

export interface CiMap {
  nodes: CiMapNode[];
  edges: CiMapEdge[];
  truncated: boolean;
}

export interface CiTicket {
  id: string;
  number: string;
  type: "Incident" | "ServiceRequest" | "Complaint" | "Inquiry" | "Suggestion" | string;
  title: string;
  state: TicketState;
  createdAt: string;
  linkedAt: string;
}

export interface TicketCi {
  item: CiPick;
  addedById: string | null;
  addedByName: string | null;
  addedAt: string;
}

/** Map filters (the API's CiMapQuery). */
export interface MapFilters {
  depth: number;
  typeIds: string[];
  classIds: string[];
  consumers: boolean;
  retired: boolean;
}

export const defaultMapFilters: MapFilters = { depth: 2, typeIds: [], classIds: [], consumers: true, retired: false };

/** The query string of the map for the filters. */
export function mapQuery(filters: MapFilters): string {
  const query = new URLSearchParams({ depth: String(filters.depth), consumers: String(filters.consumers), retired: String(filters.retired) });
  for (const id of filters.typeIds) query.append("typeIds", id);
  for (const id of filters.classIds) query.append("classIds", id);
  return query.toString();
}

/** Text in the page's language, else the other one; empty when neither is set. */
export function textIn(text: Bilingual | null | undefined, locale: Locale): string {
  return inLanguage(text, locale).value;
}

/** A node's name: number and name for items, the name for people, departments and customer groups. */
export function nodeLabel(node: CiNode, locale: Locale, unknown: string): string {
  if (node.kind === "ConfigurationItem") return [node.number, node.name].filter(Boolean).join(" ") || unknown;
  if (node.kind === "User") return node.name || unknown;
  return textIn(node.localizedName, locale) || unknown;
}

/** The family a node is drawn as: its class's family, or "People" for consumers. */
export function nodeFamily(node: CiNode): CiFamily | "People" {
  return node.kind === "ConfigurationItem" ? (node.class?.family ?? "Hardware") : "People";
}

/** One line of the map's list view: a relationship read from its source. */
export interface MapLine {
  edge: CiMapEdge;
  source: CiNode;
  target: CiNode;
}

/** The map's relationships as lines (source → type → target), in the order of the nodes' depth. */
export function mapLines(map: CiMap): MapLine[] {
  const byId = new Map(map.nodes.map((n) => [n.node.id, n]));
  return map.edges
    .filter((e) => byId.has(e.source) && byId.has(e.target))
    .map((edge) => ({ edge, source: byId.get(edge.source)!.node, target: byId.get(edge.target)!.node }))
    .sort((a, b) => Math.min(byId.get(a.edge.source)!.depth, byId.get(a.edge.target)!.depth) - Math.min(byId.get(b.edge.source)!.depth, byId.get(b.edge.target)!.depth));
}

/** A node's neighbours on the map, each with the relationship and the direction it is read in. */
export function neighbours(map: CiMap, id: string): { node: CiNode; edge: CiMapEdge; outgoing: boolean }[] {
  const byId = new Map(map.nodes.map((n) => [n.node.id, n.node]));
  return map.edges.flatMap((edge): { node: CiNode; edge: CiMapEdge; outgoing: boolean }[] => {
    if (edge.source === id && byId.has(edge.target)) return [{ node: byId.get(edge.target)!, edge, outgoing: true }];
    if (edge.target === id && byId.has(edge.source)) return [{ node: byId.get(edge.source)!, edge, outgoing: false }];
    return [];
  });
}

/** Classes in tree order under each family (the API lists them in tree order already). */
export function classesByFamily(classes: CiClass[]): { family: CiFamily; classes: CiClass[] }[] {
  return ciFamilies
    .map((family) => ({ family, classes: classes.filter((c) => c.family === family) }))
    .filter((group) => group.classes.length > 0);
}

/** Every attribute an item of the class has: inherited first (top class first), then its own. */
export function classFields(ciClass: CiClass | undefined): FormField[] {
  if (!ciClass) return [];
  return [...ciClass.inheritedAttributes.map((a) => a.field), ...(ciClass.attributes ?? [])];
}

// --- Task G2: import, discovery and reconciliation ---

export type DiscoverySourceKind = "DiscoveryApi" | "Intune" | "EntraDevices" | "Azure" | "ActiveDirectory" | "VCenter";
export const discoverySourceKinds: readonly DiscoverySourceKind[] = ["DiscoveryApi", "Intune", "EntraDevices", "Azure", "ActiveDirectory", "VCenter"];

/** The E7 connector kind a source of each kind uses (none for the discovery API and vCenter). */
export const connectorKindOf: Record<DiscoverySourceKind, "EntraId" | "Ldap" | null> = {
  DiscoveryApi: null,
  Intune: "EntraId",
  EntraDevices: "EntraId",
  Azure: "EntraId",
  ActiveDirectory: "Ldap",
  VCenter: null,
};

export interface ReconcileStats {
  received: number;
  created: number;
  updated: number;
  unchanged: number;
  linked: number;
  review: number;
  duplicates: number;
  missing: number;
  unknownClass: number;
  skipped: number;
  valuesRefused: number;
  software: number;
  relationships: number;
}

export interface DiscoverySettings {
  classMap?: Record<string, string>;
  includeApps?: boolean;
  subscriptionIds?: string[];
  resourceTypes?: string[];
  baseDn?: string | null;
  computerFilter?: string;
  url?: string | null;
  username?: string | null;
  certificateThumbprint?: string | null;
}

export interface DiscoverySource {
  id: string;
  name: string;
  kind: DiscoverySourceKind;
  connectorId: string | null;
  connectorName: string | null;
  settings: DiscoverySettings;
  hasCredential: boolean;
  credentialSetAt: string | null;
  active: boolean;
  priority: number;
  attributePriorities: Record<string, number>;
  syncMinutes: number;
  nextSyncAt: string | null;
  lastSyncAt: string | null;
  lastSyncSucceeded: boolean | null;
  lastSyncStats: ReconcileStats | null;
  lastError: string | null;
  recordCount: number;
  missingCount: number;
  reviewCount: number;
  etag: string;
}

export interface DiscoveryTest {
  ok: boolean;
  error: string | null;
  count: number;
  classes: Record<string, number>;
  sample: { id: string; class: string; name: string; attributes: Record<string, JsonValue> }[];
}

export interface DiscoverySourceRef {
  id: string;
  name: string;
  kind: DiscoverySourceKind;
}

export interface CiDiscovery {
  missingSince: string | null;
  mergedInto: { id: string; number: string; name: string; status: CiStatus } | null;
  records: { source: DiscoverySourceRef | null; externalId: string; name: string; firstSeenAt: string; lastSeenAt: string; missingSince: string | null }[];
  valueSetters: Record<string, { manual: boolean; source: DiscoverySourceRef | null }>;
  software: { name: string; publisher: string | null; version: string | null; source: DiscoverySourceRef | null; lastSeenAt: string }[];
}

export type CiReviewKind = "PossibleDuplicate" | "Ambiguous";
export type CiReviewState = "Open" | "Merged" | "KeptApart" | "Linked" | "Created" | "Closed";

export interface CiReviewSide {
  id: string;
  number: string;
  name: string;
  status: CiStatus;
  class: { id: string; key: string; family: CiFamily; path: Bilingual } | null;
  attributes: Record<string, JsonValue> | null;
  ticketCount: number;
  sources: string[];
}

export interface CiReview {
  id: string;
  kind: CiReviewKind;
  state: CiReviewState;
  matchedOn: string;
  matchedValue: string | null;
  items: CiReviewSide[];
  record: { source: string; externalId: string; name: string; class: { key: string; path: Bilingual } | null; attributes: Record<string, JsonValue> | null } | null;
  createdAt: string;
  decidedAt: string | null;
  decidedBy: string | null;
}

export interface CiReviewPage {
  items: CiReview[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Identification rules as text: one rule per line, keys joined by "+". */
export function rulesToText(rules: string[][] | null | undefined): string {
  return (rules ?? []).map((rule) => rule.join(" + ")).join("\n");
}

/** Rules typed as text back to lists (empty lines left out). */
export function textToRules(text: string): string[][] {
  return text
    .split(/\r?\n/)
    .map((line) => line.split("+").map((key) => key.trim()).filter((key) => key.length > 0))
    .filter((rule) => rule.length > 0);
}

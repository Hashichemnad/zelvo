/**
 * Service catalog and service requests (task C2, module 2) as the API returns them. See
 * docs/architecture/catalog.md. Cards are edited as a whole and saved as the working version.
 */
import type { ApprovalLevelDefinition, WorkflowCondition } from "./workflows";
import type { Bilingual, Person, TicketChannel, TicketState } from "./incidents";
import type { FormProblem } from "./forms/types";
import type { SlaClock } from "./sla";

export type ServiceState = "Draft" | "UnderReview" | "Active" | "Suspended" | "Retired";
export type ServiceVersionState = "Draft" | "UnderReview" | "Published" | "Superseded" | "Retired";
export type ServiceType = "Internal" | "External";
export type DeliveryChannel = "Portal" | "Mobile" | "WhatsApp" | "Chat" | "Email" | "CallCentre" | "ServiceCentre";
export type TargetAudience = "Individuals" | "Businesses" | "GovernmentEntities" | "Employees" | "Visitors";
export type ServiceAccessMode = "Everyone" | "Public" | "Restricted";
export type PartnerKind = "Department" | "Entity" | "Vendor";
export type ServiceStepStage = "Submitted" | "Approval" | "Assigned" | "InProgress" | "OnHold" | "Resolved" | "Closed";
/** A single service or a bundle of services (task C5). */
export type ServiceKind = "Service" | "Bundle";
export type ProactiveMode = "Offer" | "AutoStart";
export type ProactiveTriggerKind = "Date" | "Schedule" | "Event";
export type ScheduleUnit = "Day" | "Week" | "Month" | "Year";
export type ProactiveRunState = "Scheduled" | "Offered" | "Started" | "Accepted" | "Declined" | "Expired" | "Failed";
export const proactiveModes: ProactiveMode[] = ["Offer", "AutoStart"];
export const triggerKinds: ProactiveTriggerKind[] = ["Event", "Date", "Schedule"];
export const scheduleUnits: ScheduleUnit[] = ["Day", "Week", "Month", "Year"];

export const serviceStates: ServiceState[] = ["Draft", "UnderReview", "Active", "Suspended", "Retired"];
export const deliveryChannels: DeliveryChannel[] = ["Portal", "Mobile", "WhatsApp", "Chat", "Email", "CallCentre", "ServiceCentre"];
export const targetAudiences: TargetAudience[] = ["Individuals", "Businesses", "GovernmentEntities", "Employees", "Visitors"];
export const accessModes: ServiceAccessMode[] = ["Everyone", "Public", "Restricted"];
export const partnerKinds: PartnerKind[] = ["Department", "Entity", "Vendor"];
export const stepStages: ServiceStepStage[] = ["Submitted", "Approval", "Assigned", "InProgress", "OnHold", "Resolved", "Closed"];

/** Zelvo's icon set (the same list as the API's ServiceCards.Icons). */
export const serviceIcons = [
  "service", "laptop", "phone", "software", "access", "key", "email", "network", "printer", "document", "certificate",
  "id-card", "money", "payment", "calendar", "building", "car", "truck", "people", "person", "health", "education",
  "permit", "license", "home", "tools", "support", "question", "shield", "globe",
] as const;

/** A short symbol per icon, shown in a tile (decorative; the service name carries the meaning). */
export const iconGlyph: Record<string, string> = {
  service: "◆", laptop: "💻", phone: "📱", software: "🧩", access: "🔓", key: "🔑", email: "✉️", network: "🌐", printer: "🖨️",
  document: "📄", certificate: "📜", "id-card": "🪪", money: "💰", payment: "💳", calendar: "📅", building: "🏢", car: "🚗",
  truck: "🚚", people: "👥", person: "👤", health: "⚕️", education: "🎓", permit: "📋", license: "🏷️", home: "🏠", tools: "🛠️",
  support: "🛟", question: "❓", shield: "🛡️", globe: "🌍",
};

export interface Text {
  en: string | null;
  ar: string | null;
}

export interface ServiceDocument {
  key: string;
  name: Text;
  description: Text;
  required: boolean;
  fileTypes: string[];
  maxSizeMb: number | null;
  templateFileId: string | null;
}

export interface ServiceStep {
  name: Text;
  description: Text;
  stage: ServiceStepStage;
  /** A fulfilment task whose start reaches the step (task C4). */
  taskKey: string | null;
}

export type AssignmentMethod = "GroupQueue" | "RoundRobin";

/** An escalation threshold of a service's SLA (task C4). */
export interface ServiceEscalation {
  metric: "Response" | "Resolution" | null;
  percent: number;
  notifyAssignee: boolean;
  notifyGroupLeads: boolean;
  notifyGroupId: string | null;
}

/** A task of a service's template (task C4). */
export interface FulfilmentTask {
  key: string;
  name: Text;
  description: Text;
  groupId: string | null;
  parallelWithPrevious: boolean;
  targetMinutes: number | null;
}

/** How a service is fulfilled (task C4): first group, overrides by answer, its own SLA and the task template. */
export interface ServiceFulfilment {
  groupId: string | null;
  method: AssignmentMethod;
  overrides: { conditions: WorkflowCondition[]; groupId: string | null; method: AssignmentMethod }[];
  ownershipMinutes: number | null;
  completionMinutes: number | null;
  escalations: ServiceEscalation[];
  tasks: FulfilmentTask[];
}

export type RequestTaskState = "Waiting" | "Open" | "InProgress" | "Done" | "Cancelled";

/** A fulfilment task of a request (task C4). */
export interface RequestTask {
  id: string;
  requestId: string;
  requestNumber: string;
  requestTitle: string;
  key: string;
  name: Text;
  description: Text;
  position: number;
  stage: number;
  state: RequestTaskState;
  group: { id: string; name: Text } | null;
  assignee: Person | null;
  targetMinutes: number | null;
  dueAt: string | null;
  overdue: boolean;
  notes: string | null;
  openedAt: string | null;
  startedAt: string | null;
  completedAt: string | null;
  completedBy: Person | null;
  mayWork: boolean;
  mayAssign: boolean;
  etag: string;
}

/** A service in a bundle (task C5). */
export interface BundleItem {
  serviceId: string | null;
  required: boolean;
  parallelWithPrevious: boolean;
}

/** One trigger of a proactive service (task C5). */
export interface ProactiveTrigger {
  key: string;
  kind: ProactiveTriggerKind;
  sourceServiceId: string | null;
  sourceFieldKey: string | null;
  offsetDays: number;
  startOn: string | null;
  every: number;
  unit: ScheduleUnit;
  people: { groupIds: string[]; roleKeys: string[]; departmentIds: string[]; userIds: string[] };
}

/** When Zelvo offers or starts the service by itself (task C5). */
export interface ServiceProactive {
  enabled: boolean;
  mode: ProactiveMode;
  offerDays: number;
  triggers: ProactiveTrigger[];
}

export function newTrigger(kind: ProactiveTriggerKind, index: number): ProactiveTrigger {
  return {
    key: `${kind.toLowerCase()}${index + 1}`, kind, sourceServiceId: null, sourceFieldKey: null, offsetDays: kind === "Date" ? -30 : 0,
    startOn: null, every: 1, unit: "Year", people: { groupIds: [], roleKeys: [], departmentIds: [], userIds: [] },
  };
}

/**
 * The stage each bundle item starts in (0 first): an item "together with the one before" shares
 * its stage, the others start a new one, as the API works it out.
 */
export function bundleStages(items: readonly { parallelWithPrevious: boolean }[]): number[] {
  let stage = -1;
  return items.map((item) => (stage = item.parallelWithPrevious && stage >= 0 ? stage : stage + 1));
}

/** A service's fee; online payment of it came with task E8. */
export interface ServiceFee {
  paid: boolean;
  amountAed: number | null;
  notes: Text;
  timing: import("./payments").PaymentTiming;
  gatewayId: string | null;
  rules: { name: Text; amountAed: number; conditions: WorkflowCondition[] }[];
}

export interface ServiceCard {
  name: Text;
  summary: Text;
  description: Text;
  parentId: string | null;
  catalogIds: string[];
  categoryIds: string[];
  departmentId: string | null;
  ownerId: string | null;
  type: ServiceType;
  externalUrl: string | null;
  formId: string | null;
  documents: ServiceDocument[];
  steps: ServiceStep[];
  fee: ServiceFee;
  days: number | null;
  channels: DeliveryChannel[];
  audiences: TargetAudience[];
  partners: { kind: PartnerKind; departmentId: string | null; name: Text }[];
  relatedServiceIds: string[];
  prerequisiteServiceIds: string[];
  keywords: string[];
  icon: string | null;
  imageFileId: string | null;
  faq: { question: Text; answer: Text }[];
  access: { mode: ServiceAccessMode; groupIds: string[]; roleKeys: string[]; departmentIds: string[]; locationIds: string[]; userIds: string[] };
  onBehalfRoleKeys: string[];
  /** Approvals before assignment and after fulfilment (task C3). */
  approvals: { beforeAssignment: ApprovalLevelDefinition[]; afterFulfilment: ApprovalLevelDefinition[] };
  /** Initial group, service SLA and tasks (task C4). */
  fulfilment: ServiceFulfilment;
  /** A single service or a bundle (task C5). */
  kind: ServiceKind;
  bundle: { items: BundleItem[] };
  proactive: ServiceProactive;
}

export const emptyText = (): Text => ({ en: null, ar: null });

/** A card with every list and object present (the API may leave some out). */
export function completeCard(card: Partial<ServiceCard> | null | undefined): ServiceCard {
  const c = card ?? {};
  const text = (t: Partial<Text> | null | undefined): Text => ({ en: t?.en ?? null, ar: t?.ar ?? null });
  return {
    name: text(c.name), summary: text(c.summary), description: text(c.description),
    parentId: c.parentId ?? null, catalogIds: c.catalogIds ?? [], categoryIds: c.categoryIds ?? [],
    departmentId: c.departmentId ?? null, ownerId: c.ownerId ?? null, type: c.type ?? "Internal", externalUrl: c.externalUrl ?? null,
    formId: c.formId ?? null,
    documents: (c.documents ?? []).map((d) => ({ key: d.key ?? "", name: text(d.name), description: text(d.description), required: !!d.required, fileTypes: d.fileTypes ?? [], maxSizeMb: d.maxSizeMb ?? null, templateFileId: d.templateFileId ?? null })),
    steps: (c.steps ?? []).map((s) => ({ name: text(s.name), description: text(s.description), stage: s.stage ?? "InProgress", taskKey: s.taskKey ?? null })),
    fee: {
      paid: !!c.fee?.paid, amountAed: c.fee?.amountAed ?? null, notes: text(c.fee?.notes),
      timing: c.fee?.timing ?? "OnSubmission", gatewayId: c.fee?.gatewayId ?? null,
      rules: (c.fee?.rules ?? []).map((r) => ({ name: text(r.name), amountAed: r.amountAed ?? 0, conditions: r.conditions ?? [] })),
    },
    days: c.days ?? null, channels: c.channels ?? ["Portal"], audiences: c.audiences ?? [],
    partners: (c.partners ?? []).map((p) => ({ kind: p.kind ?? "Entity", departmentId: p.departmentId ?? null, name: text(p.name) })),
    relatedServiceIds: c.relatedServiceIds ?? [], prerequisiteServiceIds: c.prerequisiteServiceIds ?? [], keywords: c.keywords ?? [],
    icon: c.icon ?? null, imageFileId: c.imageFileId ?? null,
    faq: (c.faq ?? []).map((f) => ({ question: text(f.question), answer: text(f.answer) })),
    access: {
      mode: c.access?.mode ?? "Everyone", groupIds: c.access?.groupIds ?? [], roleKeys: c.access?.roleKeys ?? [],
      departmentIds: c.access?.departmentIds ?? [], locationIds: c.access?.locationIds ?? [], userIds: c.access?.userIds ?? [],
    },
    onBehalfRoleKeys: c.onBehalfRoleKeys ?? [],
    approvals: { beforeAssignment: c.approvals?.beforeAssignment ?? [], afterFulfilment: c.approvals?.afterFulfilment ?? [] },
    fulfilment: {
      groupId: c.fulfilment?.groupId ?? null, method: c.fulfilment?.method ?? "GroupQueue", overrides: c.fulfilment?.overrides ?? [],
      ownershipMinutes: c.fulfilment?.ownershipMinutes ?? null, completionMinutes: c.fulfilment?.completionMinutes ?? null,
      escalations: c.fulfilment?.escalations ?? [],
      tasks: (c.fulfilment?.tasks ?? []).map((t) => ({ key: t.key ?? "", name: text(t.name), description: text(t.description), groupId: t.groupId ?? null,
        parallelWithPrevious: !!t.parallelWithPrevious, targetMinutes: t.targetMinutes ?? null })),
    },
    kind: c.kind ?? "Service",
    bundle: { items: (c.bundle?.items ?? []).map((i) => ({ serviceId: i.serviceId ?? null, required: i.required ?? true, parallelWithPrevious: !!i.parallelWithPrevious })) },
    proactive: {
      enabled: !!c.proactive?.enabled, mode: c.proactive?.mode ?? "Offer", offerDays: c.proactive?.offerDays ?? 14,
      triggers: (c.proactive?.triggers ?? []).map((t) => ({
        key: t.key ?? "", kind: t.kind ?? "Event", sourceServiceId: t.sourceServiceId ?? null, sourceFieldKey: t.sourceFieldKey ?? null,
        offsetDays: t.offsetDays ?? 0, startOn: t.startOn ?? null, every: t.every ?? 1, unit: t.unit ?? "Year",
        people: { groupIds: t.people?.groupIds ?? [], roleKeys: t.people?.roleKeys ?? [], departmentIds: t.people?.departmentIds ?? [], userIds: t.people?.userIds ?? [] },
      })),
    },
  };
}

export interface ServiceVersion {
  id: string;
  number: number;
  state: ServiceVersionState;
  card: ServiceCard | null;
  changeNote: string | null;
  createdBy: string | null;
  lastEditedBy: string | null;
  submittedBy: string | null;
  submittedAt: string | null;
  reviewedBy: string | null;
  reviewComment: string | null;
  publishedAt: string | null;
  updatedAt: string;
}

export interface ServiceFile {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  scanStatus: "Pending" | "Clean" | "Infected" | "NotScanned";
  uploadedAt: string;
}

/** What the API allows the caller to do with a service (`actions`). */
export type ServiceAction =
  | "edit" | "submit" | "approve" | "sendBack" | "discard" | "suspend" | "resume" | "retire" | "reopen" | "delete" | "copy" | "saveAsTemplate";

export interface ServiceDetail {
  id: string;
  code: string;
  state: ServiceState;
  name: Bilingual;
  parentId: string | null;
  catalogIds: string[];
  suspendedMessage: Bilingual;
  published: ServiceVersion | null;
  working: ServiceVersion | null;
  problems: FormProblem[];
  files: ServiceFile[];
  actions: ServiceAction[];
  requestCount: number;
  etag: string;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
  retiredAt: string | null;
}

export interface ServiceSummary {
  id: string;
  code: string;
  state: ServiceState;
  name: Bilingual;
  parentId: string | null;
  catalogIds: string[];
  categoryIds: string[];
  publishedVersion: number | null;
  workingVersion: number | null;
  workingState: ServiceVersionState | null;
  submittedAt: string | null;
  mayEdit: boolean;
  updatedAt: string;
}

export interface CatalogEditor {
  userId: string | null;
  groupId: string | null;
  name: string | null;
}

export interface Catalog {
  id: string;
  key: string;
  name: Bilingual;
  description: Bilingual;
  departmentId: string | null;
  position: number;
  active: boolean;
  editors: CatalogEditor[];
  serviceCount: number;
  youEdit: boolean;
  etag: string;
  updatedAt: string;
}

export interface ServiceTemplate {
  id: string;
  builtIn: boolean;
  name: Bilingual;
  description: Bilingual;
  card: ServiceCard;
  createdAt: string | null;
}

/** A service in the catalog for requesters. */
export interface PublishedServiceSummary {
  id: string;
  code: string;
  state: ServiceState;
  type: ServiceType;
  name: Bilingual;
  summary: Bilingual;
  icon: string | null;
  imageFileId: string | null;
  parentId: string | null;
  catalogIds: string[];
  categoryIds: string[];
  paid: boolean;
  amountAed: number | null;
  days: number | null;
  subServiceCount: number;
}

export interface CatalogBrowse {
  catalogs: { id: string; key: string; name: Bilingual; description: Bilingual; serviceCount: number }[];
  services: PublishedServiceSummary[];
}

export interface PublishedService {
  id: string;
  code: string;
  state: ServiceState;
  suspendedMessage: Bilingual;
  type: ServiceType;
  externalUrl: string | null;
  name: Bilingual;
  summary: Bilingual;
  description: Bilingual;
  icon: string | null;
  imageFileId: string | null;
  department: Bilingual | null;
  owner: string | null;
  categories: { id: string; name: Bilingual }[];
  catalogs: { id: string; name: Bilingual }[];
  documents: { key: string; name: Bilingual; description: Bilingual; required: boolean; fileTypes: string[]; maxSizeMb: number | null; templateFileId: string | null }[];
  steps: { name: Bilingual; description: Bilingual; stage: ServiceStepStage }[];
  fee: { paid: boolean; amountAed: number | null; notes: Bilingual };
  days: number | null;
  channels: DeliveryChannel[];
  audiences: TargetAudience[];
  partners: { kind: PartnerKind; name: Bilingual }[];
  faq: { question: Bilingual; answer: Bilingual }[];
  parent: PublishedServiceSummary | null;
  subServices: PublishedServiceSummary[];
  related: PublishedServiceSummary[];
  prerequisites: { service: PublishedServiceSummary; completed: boolean }[];
  hasForm: boolean;
  mayRequest: boolean;
  mayRequestOnBehalf: boolean;
  signInToRequest: boolean;
  /** Task C5: a bundle and its services (those the caller may see), with the stage each starts in. */
  kind?: ServiceKind;
  bundleItems?: { service: PublishedServiceSummary; required: boolean; parallelWithPrevious: boolean; stage: number }[];
}

/** A bundle's combined form (task C5): its own form and each service's form, documents and fee. */
export interface BundleForm {
  form: import("./forms/types").ResolvedForm;
  parts: {
    serviceId: string;
    name: Bilingual | null;
    code: string | null;
    required: boolean;
    stage: number;
    available: boolean;
    documents: PublishedService["documents"];
    fee: PublishedService["fee"] | null;
    form: import("./forms/types").ResolvedForm | null;
  }[];
}

/** A part of a bundle request on its tracking screen (task C5). */
export interface BundlePart {
  id: string;
  number: string;
  title: string;
  state: TicketState;
  service: { id: string; code: string; name: Bilingual } | null;
  stage: number;
  optional: boolean;
  held: boolean;
  awaitingApproval: boolean;
  group: { id: string; name: Bilingual } | null;
  stepCount: number;
  currentStep: number;
}

/** A service Zelvo offers the person (task C5); answers fill in its form. */
export interface ServiceOffer {
  id: string;
  service: { id: string; code: string; name: Bilingual };
  summary: Bilingual;
  state: ProactiveRunState;
  offeredAt: string | null;
  expiresAt: string | null;
  kind: ServiceKind;
  answers: Record<string, unknown> | null;
}

/** A time a proactive service was due for someone (task C5). */
export interface ProactiveRun {
  id: string;
  service: { id: string; code: string; name: Bilingual } | null;
  trigger: string;
  kind: ProactiveTriggerKind;
  person: Person;
  occurrence: string;
  dueOn: string;
  state: ProactiveRunState;
  reason: string | null;
  request: { id: string; number: string } | null;
  source: { id: string; number: string } | null;
  offeredAt: string | null;
  expiresAt: string | null;
  endedAt: string | null;
  createdAt: string;
}

export interface ServiceRequest {
  id: string;
  number: string;
  title: string;
  description: string | null;
  state: TicketState;
  channel: TicketChannel;
  requester: Person;
  requestedBy: Person | null;
  assignee: Person | null;
  group: { id: string; name: Bilingual } | null;
  service: { id: string; code: string; name: Bilingual } | null;
  steps: { name: Bilingual; description: Bilingual; stage: ServiceStepStage }[];
  currentStep: number;
  onHoldReason: string | null;
  resolutionNotes: string | null;
  nextStates: TicketState[];
  /** Waiting for an approval (task C3): before assignment, or the sign-off after fulfilment. */
  awaitingApproval: boolean;
  sla: SlaClock[];
  etag: string;
  createdAt: string;
  updatedAt: string;
  resolvedAt: string | null;
  closedAt: string | null;
  /** Task C5: a bundle with its parts, or a part of a bundle. */
  isBundle?: boolean;
  held?: boolean;
  hold?: "None" | "Bundle" | "Payment";
  bundle?: { id: string; number: string } | null;
  parts?: BundlePart[] | null;
}

export interface ServiceRequestPage {
  items: ServiceRequest[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ServiceRequestAttachment {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  scanStatus: "Pending" | "Clean" | "Infected" | "NotScanned";
  documentKey: string | null;
  formFieldKey: string | null;
  uploadedAt: string;
}

/** The /form-uploads field of a required document: "document" and the key with a capital letter (passportCopy → documentPassportCopy). */
export function uploadFieldOf(documentKey: string): string {
  return documentKey ? `document${documentKey[0]!.toUpperCase()}${documentKey.slice(1)}` : "document";
}

/** A problem's field ("documents.0.key") as the section it belongs to, for grouping. */
export function problemSection(field: string | null | undefined): string {
  return (field ?? "").split(".")[0] ?? "";
}

/** Permissions that open Settings → Catalog. */
export function isCatalogStaff(permissions: readonly string[]): boolean {
  return permissions.includes("catalog.manage") || permissions.includes("catalog.publish");
}

/** Bilingual input for the API: blanks become null. */
export function cleanText(text: Text): Text {
  const clean = (v: string | null) => (v && v.trim() ? v.trim() : null);
  return { en: clean(text.en), ar: clean(text.ar) };
}

/** A card ready to send: only {en, ar} in texts, blanks removed. */
export function cardForApi(card: ServiceCard): ServiceCard {
  return {
    ...card,
    name: cleanText(card.name), summary: cleanText(card.summary), description: cleanText(card.description),
    externalUrl: card.externalUrl?.trim() || null,
    documents: card.documents.map((d) => ({ ...d, key: d.key.trim(), name: cleanText(d.name), description: cleanText(d.description) })),
    fee: {
      ...card.fee, notes: cleanText(card.fee.notes), amountAed: card.fee.paid ? card.fee.amountAed : null,
      rules: card.fee.paid ? card.fee.rules.map((r) => ({ ...r, name: cleanText(r.name) })) : [],
    },
    partners: card.partners.map((p) => ({ ...p, name: cleanText(p.name) })),
    faq: card.faq.map((f) => ({ question: cleanText(f.question), answer: cleanText(f.answer) })),
    keywords: card.keywords.map((k) => k.trim()).filter(Boolean),
    steps: card.steps.map((s) => ({ ...s, name: cleanText(s.name), description: cleanText(s.description), taskKey: s.taskKey || null })),
    fulfilment: {
      ...card.fulfilment,
      tasks: card.fulfilment.tasks.map((t) => ({ ...t, key: t.key.trim(), name: cleanText(t.name), description: cleanText(t.description) })),
    },
    approvals: {
      beforeAssignment: card.approvals.beforeAssignment.map((l) => ({ ...l, name: cleanText(l.name) })),
      afterFulfilment: card.approvals.afterFulfilment.map((l) => ({ ...l, name: cleanText(l.name) })),
    },
  };
}

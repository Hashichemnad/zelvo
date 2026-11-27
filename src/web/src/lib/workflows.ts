/**
 * Workflow rules and approvals (task C3, modules 2 and 10) as the API returns them. Rules are
 * when / if / then: a trigger, conditions and actions (assign, notify, approval). See
 * docs/architecture/workflows.md.
 */
import type { Bilingual, Person } from "./incidents";
import type { TicketState } from "./incidents";

export type WorkflowTrigger = "TicketCreated" | "StateChanged" | "AssignmentChanged" | "ApprovalDecided";
export type RecordType = "Incident" | "ServiceRequest";
export type ConditionMatch = "All" | "Any";
export type ApprovalStage = "BeforeAssignment" | "AfterFulfilment";
export type ApprovalMode = "Any" | "All";
export type ApproverKind = "RequesterManager" | "User" | "Group" | "GroupLeads" | "Role" | "AnswerUser";
export type ApprovalDecisionState = "Pending" | "Approved" | "Rejected" | "NotNeeded";
export type ApprovalProcessState = "Pending" | "Approved" | "Rejected" | "Cancelled";
export type ApprovalLevelState = "Waiting" | "Approved" | "Rejected" | "NotStarted" | "Cancelled";

export const workflowTriggers: WorkflowTrigger[] = ["TicketCreated", "StateChanged", "AssignmentChanged", "ApprovalDecided"];
export const recordTypes: RecordType[] = ["Incident", "ServiceRequest"];
export const approverKinds: ApproverKind[] = ["RequesterManager", "User", "Group", "GroupLeads", "Role", "AnswerUser"];

/** Fields conditions look at; `answer` stands for `answer.<fieldKey>`. */
export type ConditionField =
  | "service"
  | "category"
  | "location"
  | "group"
  | "requesterDepartment"
  | "requesterCustomerGroup"
  | "channel"
  | "state"
  | "requesterLanguage"
  | "priority"
  | "fee"
  | "answer";

export const idListFields: ConditionField[] = ["service", "category", "location", "group", "requesterDepartment", "requesterCustomerGroup"];
export const nameListFields: ConditionField[] = ["channel", "state", "requesterLanguage"];
export const numberFields: ConditionField[] = ["priority", "fee"];
export const conditionFields: ConditionField[] = [...idListFields, ...nameListFields, ...numberFields, "answer"];
export const listOperators = ["in", "notIn"] as const;
export const numberOperators = ["eq", "neq", "gt", "gte", "lt", "lte"] as const;
export const answerOperators = [...numberOperators, "contains", "empty", "notEmpty"] as const;
export type ConditionOperator = (typeof answerOperators)[number] | (typeof listOperators)[number];

export const channelValues = ["Portal", "Email", "Chat", "WhatsApp", "Phone", "Api", "AiAgent", "Proactive"] as const;
export const stateValues: TicketState[] = ["New", "Assigned", "InProgress", "OnHold", "Resolved", "Closed", "Reopened"];

export interface WorkflowCondition {
  field: string;
  op: ConditionOperator;
  value: string | number | string[] | null;
}

export interface ApproverRef {
  kind: ApproverKind;
  id?: string | null;
  key?: string | null;
}

export interface ApprovalLevelDefinition {
  name: Bilingual;
  mode: ApprovalMode;
  approvers: ApproverRef[];
  conditions: WorkflowCondition[];
}

export interface AssignAction {
  type: "assign";
  groupId: string | null;
  method: "GroupQueue" | "RoundRobin";
  assigneeId: string | null;
}

/** Task K1: messaging channels a rule can also notify by (E3). */
export type MessagingKind = "WhatsApp" | "Sms" | "Teams";
export const messagingKinds: MessagingKind[] = ["WhatsApp", "Sms", "Teams"];

export interface NotifyAction {
  type: "notify";
  recipients: string[];
  subject: Bilingual;
  body: Bilingual;
  email: boolean;
  /** Task K1: also by WhatsApp, SMS or Teams; null for rules made before K1 (each person's own choice). */
  channels?: MessagingKind[] | null;
}

export interface ApprovalAction {
  type: "approval";
  stage: ApprovalStage;
  levels: ApprovalLevelDefinition[];
}

/** Task E7: call an operation of a REST connector (the no-code connector builder). */
export interface ConnectorAction {
  type: "connector";
  connectorId: string | null;
  operation: string | null;
}

export type WorkflowAction = AssignAction | NotifyAction | ApprovalAction | ConnectorAction;

/** A REST connector and its operations, for the rule builder (task E7). */
export interface RestConnectorOperations {
  id: string;
  name: string;
  active: boolean;
  operations: { key: string; name: Bilingual }[];
}

export interface WorkflowRule {
  id: string;
  name: Bilingual;
  description: Bilingual;
  position: number;
  active: boolean;
  trigger: WorkflowTrigger;
  recordType: RecordType | null;
  match: ConditionMatch;
  conditions: WorkflowCondition[];
  actions: WorkflowAction[];
  stopProcessing: boolean;
  etag: string;
  createdAt: string;
  updatedAt: string;
  /** Task K1: made by the AI from `sourceText`; does not run until approved. */
  draft: boolean;
  sourceText: string | null;
  sourceLanguage: "en" | "ar" | null;
  /** What the AI could not express with the builder. */
  unmapped: string[];
  approvedAt: string | null;
}

/** Whether plain-language rules can be made now (task K1). */
export interface PlainLanguageAvailability {
  available: boolean;
  reason: string | null;
}

export interface WorkflowRuleInput {
  name: Bilingual;
  description: Bilingual;
  active: boolean;
  trigger: WorkflowTrigger;
  recordType: RecordType | null;
  match: ConditionMatch;
  conditions: WorkflowCondition[];
  actions: WorkflowAction[];
  stopProcessing: boolean;
}

export interface RuleTestResult {
  id: string;
  name: Bilingual;
  holds: boolean;
  actions: string[];
  draft?: boolean;
}

/** A problem the API found in a rule or a card. */
export interface WorkflowProblem {
  field: string | null;
  code: string;
  message: string;
}

/** Recipients of notify actions besides `user:`, `group:` and `role:`. */
export const fixedRecipients = ["requester", "requesterManager", "assignee", "group", "groupLeads"] as const;

/** Placeholders rule wording may use. */
export const notifyPlaceholders = [
  "recipient.name",
  "tenant.name",
  "ticket.number",
  "ticket.title",
  "ticket.state",
  "ticket.group",
  "requester.name",
  "service.name",
] as const;

export interface ServiceRef {
  id: string;
  code: string;
  name: Bilingual;
}

export interface ApprovalTicketRef {
  id: string;
  number: string;
  title: string;
  type: RecordType;
  state: TicketState;
  requester: Person;
  service: ServiceRef | null;
  createdAt: string;
}

export interface ApprovalSummary {
  id: string;
  processId: string;
  stage: ApprovalStage;
  level: number;
  levelName: Bilingual;
  state: ApprovalDecisionState;
  comment: string | null;
  createdAt: string;
  decidedAt: string | null;
  approver: Person;
  ticket: ApprovalTicketRef;
}

export interface ApprovalDecisionView {
  id: string;
  approver: Person;
  state: ApprovalDecisionState;
  comment: string | null;
  decidedAt: string | null;
}

export interface ApprovalLevelView {
  index: number;
  name: Bilingual;
  mode: ApprovalMode;
  state: ApprovalLevelState;
  fallback: boolean;
  decisions: ApprovalDecisionView[];
}

export interface ApprovalProcessView {
  id: string;
  stage: ApprovalStage;
  state: ApprovalProcessState;
  currentLevel: number;
  reason: string | null;
  createdAt: string;
  endedAt: string | null;
  levels: ApprovalLevelView[];
}

export interface ApprovalDetail {
  approval: ApprovalSummary;
  description: string | null;
  form: unknown;
  process: ApprovalProcessView;
  mayDecide: boolean;
}

export interface ApprovalPage {
  items: ApprovalSummary[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** A new, empty approval level. */
export function emptyLevel(): ApprovalLevelDefinition {
  return { name: { en: null, ar: null }, mode: "Any", approvers: [{ kind: "RequesterManager" }], conditions: [] };
}

/** A new condition with sensible defaults for its field. */
export function emptyCondition(field: ConditionField = "service"): WorkflowCondition {
  if (numberFields.includes(field)) return { field, op: "gt", value: 0 };
  if (field === "answer") return { field: "answer.", op: "eq", value: "" };
  return { field, op: "in", value: [] };
}

/** The field kind of a stored condition field (`answer.monitorSize` → `answer`). */
export function fieldKind(field: string): ConditionField {
  return field.startsWith("answer.") ? "answer" : (field as ConditionField);
}

/** A new action of a type. */
export function emptyAction(type: WorkflowAction["type"]): WorkflowAction {
  switch (type) {
    case "assign":
      return { type, groupId: null, method: "GroupQueue", assigneeId: null };
    case "notify":
      return { type, recipients: ["requester"], subject: { en: null, ar: null }, body: { en: null, ar: null }, email: true, channels: [] };
    case "connector":
      return { type, connectorId: null, operation: null };
    default:
      return { type: "approval", stage: "BeforeAssignment", levels: [emptyLevel()] };
  }
}

/** Approval actions are allowed only on service requests when they are submitted. */
export function approvalAllowed(trigger: WorkflowTrigger, recordType: RecordType | null): boolean {
  return trigger === "TicketCreated" && recordType === "ServiceRequest";
}

/** The rule as the API takes it. */
export function toRuleInput(rule: WorkflowRule): WorkflowRuleInput {
  const { name, description, active, trigger, recordType, match, conditions, actions, stopProcessing } = rule;
  return { name, description, active, trigger, recordType, match, conditions, actions, stopProcessing };
}

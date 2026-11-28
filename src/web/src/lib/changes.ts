/**
 * Change management (task H3, module 17) as the API returns it: changes with their stage, risk,
 * plans, window, approval and conflicts; the calendar, blackout periods, standard change templates
 * and Settings → Changes. The server is Zelvo.Api/Changes; keep the two equal.
 */
import type { Bilingual, Person, TicketState } from "./incidents";
import type { NamedRef, PathRef } from "./directory";
import type { ApprovalLevelDefinition, ApprovalProcessState } from "./workflows";

export type ChangeKind = "Standard" | "Normal" | "Emergency";
export const changeKinds: readonly ChangeKind[] = ["Standard", "Normal", "Emergency"];

export type ChangeStage = "New" | "Authorisation" | "Scheduled" | "Implementation" | "Review" | "Closed";
export const changeStages: readonly ChangeStage[] = ["New", "Authorisation", "Scheduled", "Implementation", "Review", "Closed"];

export type ChangeRiskLevel = "Low" | "Medium" | "High";
export const riskLevels: readonly ChangeRiskLevel[] = ["Low", "Medium", "High"];

export type ChangeOutcome = "Successful" | "SuccessfulWithProblems" | "Failed" | "BackedOut";
export const changeOutcomes: readonly ChangeOutcome[] = ["Successful", "SuccessfulWithProblems", "Failed", "BackedOut"];

export type ChangeClosure = "Completed" | "Rejected" | "Cancelled";

export type ChangeAction = "Submit" | "Replan" | "StartImplementation" | "Implemented" | "Review" | "Cancel";

export interface ConflictItem {
  id: string;
  number: string;
  name: string;
}

export interface ChangeConflict {
  kind: "change" | "blackout";
  start: string;
  end: string;
  items: ConflictItem[];
  changeId: string | null;
  changeNumber: string | null;
  changeTitle: string | null;
  changeStage: ChangeStage | null;
  blackoutId: string | null;
  blackoutName: Bilingual | null;
}

export interface ChangeApprovalSummary {
  processId: string;
  state: ApprovalProcessState;
  currentLevel: number;
  levels: number;
  levelName: Bilingual | null;
  reason: string | null;
  createdAt: string;
  endedAt: string | null;
  /** Task H4: the board the change waits for. */
  board?: "Advisory" | "Emergency" | null;
  /** Task H4: when the emergency board is expected to decide. */
  decideBy?: string | null;
}

export interface Change {
  id: string;
  number: string;
  title: string;
  description: string | null;
  kind: ChangeKind;
  stage: ChangeStage;
  state: TicketState;
  phase: "Planning" | "Authorisation" | "Scheduled" | "Implementation";
  closure: ChangeClosure | null;
  template: NamedRef | null;
  riskAnswers: Record<string, string>;
  riskScore: number | null;
  riskLevel: ChangeRiskLevel | null;
  implementationPlan: string | null;
  testPlan: string | null;
  backoutPlan: string | null;
  plannedStart: string | null;
  plannedEnd: string | null;
  actualStart: string | null;
  actualEnd: string | null;
  outcome: ChangeOutcome | null;
  reviewNotes: string | null;
  reviewedBy: Person | null;
  reviewedAt: string | null;
  category: PathRef | null;
  group: NamedRef | null;
  assignee: Person | null;
  createdBy: Person | null;
  resolutionNotes: string | null;
  approval: ChangeApprovalSummary | null;
  taskCount: number;
  openTaskCount: number;
  itemCount: number;
  nextActions: ChangeAction[];
  etag: string;
  createdAt: string;
  updatedAt: string;
  submittedAt: string | null;
  approvedAt: string | null;
  resolvedAt: string | null;
  closedAt: string | null;
  conflicts?: ChangeConflict[] | null;
}

export interface ChangePage {
  items: Change[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ChangeLink {
  id: string;
  number: string;
  title: string;
  type: "Problem" | "Incident";
  state: TicketState;
  kind: "Fixes" | "CausedBy";
  linkedAt: string;
}

export interface ChangeComment {
  id: string;
  body: string;
  language: string;
  author: Person;
  createdAt: string;
}

export interface CalendarChange {
  id: string;
  number: string;
  title: string;
  kind: ChangeKind;
  stage: ChangeStage;
  riskLevel: ChangeRiskLevel | null;
  start: string;
  end: string;
  items: ConflictItem[];
  conflict: boolean;
}

export interface Blackout {
  id: string;
  name: Bilingual;
  startsAt: string;
  endsAt: string;
  item: ConflictItem | null;
  etag: string;
}

export interface ChangeCalendar {
  from: string;
  to: string;
  changes: CalendarChange[];
  blackouts: Blackout[];
}

export interface TemplateTask {
  key: string;
  name: Bilingual;
  description: Bilingual;
  group: NamedRef | null;
  parallelWithPrevious: boolean;
  targetMinutes: number | null;
}

export interface ChangeTemplate {
  id: string;
  name: Bilingual;
  description: Bilingual;
  category: PathRef | null;
  group: NamedRef | null;
  implementationPlan: string | null;
  testPlan: string | null;
  backoutPlan: string | null;
  tasks: TemplateTask[];
  active: boolean;
  changeCount: number;
  etag: string;
  updatedAt: string;
}

export interface Text2 {
  en: string | null;
  ar: string | null;
}

export interface RiskOption {
  key: string;
  text: Text2;
  weight: number;
}

export interface RiskQuestion {
  key: string;
  text: Text2;
  options: RiskOption[];
  active: boolean;
}

export interface ChangeApprovalPaths {
  low: ApprovalLevelDefinition[];
  medium: ApprovalLevelDefinition[];
  high: ApprovalLevelDefinition[];
  emergency: ApprovalLevelDefinition[];
}

export interface ChangeSettings {
  questions: RiskQuestion[];
  mediumFrom: number;
  highFrom: number;
  paths: ChangeApprovalPaths;
}

/** The score and level of some answers, worked out like the server does (shown while answering). */
export function assess(settings: ChangeSettings, answers: Record<string, string>): { score: number; level: ChangeRiskLevel; unanswered: number } {
  let score = 0;
  let unanswered = 0;
  for (const q of settings.questions) {
    const option = q.options.find((o) => o.key === answers[q.key]);
    if (option) score += option.weight;
    else if (q.active) unanswered++;
  }
  const level: ChangeRiskLevel = score >= settings.highFrom ? "High" : score >= settings.mediumFrom ? "Medium" : "Low";
  return { score, level, unanswered };
}

/** A local date-and-time input's value (yyyy-mm-ddThh:mm) for an ISO instant, in the browser's time zone. */
export function toLocalInput(iso: string | null): string {
  if (!iso) return "";
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** An ISO instant for a local date-and-time input's value, or null when empty. */
export function fromLocalInput(value: string): string | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

// ---- Task H4: the change advisory board and the emergency board ----

export type BoardKind = "Advisory" | "Emergency";
export type MeetingState = "Planned" | "Held" | "Cancelled";
export type CabDecision = "Approved" | "Rejected" | "Deferred";
export type VoteChoice = "Approve" | "Reject" | "Abstain";
export const voteChoices: readonly VoteChoice[] = ["Approve", "Reject", "Abstain"];
export const cabDecisions: readonly CabDecision[] = ["Approved", "Rejected", "Deferred"];

export interface ChangeBoard {
  id: string;
  kind: BoardKind;
  name: Bilingual;
  chair: Person | null;
  deputyChair: Person | null;
  members: Person[];
  decideWithinHours: number;
  etag: string;
}

export interface MeetingSummary {
  id: string;
  title: Bilingual;
  startsAt: string;
  endsAt: string;
  location: string | null;
  chair: Person;
  state: MeetingState;
  itemCount: number;
  decidedCount: number;
}

export interface Vote {
  member: Person;
  choice: VoteChoice;
  comment: string | null;
  votedAt: string;
}

export interface AgendaItem {
  id: string;
  position: number;
  changeId: string;
  changeNumber: string;
  changeTitle: string;
  kind: ChangeKind;
  stage: ChangeStage;
  riskLevel: ChangeRiskLevel | null;
  plannedStart: string | null;
  plannedEnd: string | null;
  conflictCount: number;
  waiting: boolean;
  votes: Vote[];
  tally: Record<VoteChoice, number>;
  decision: CabDecision | null;
  comment: string | null;
  minutes: string | null;
  decidedBy: Person | null;
  decidedAt: string | null;
  /** Task H5: the what-if figures on the agenda. */
  predicted?: PredictedRisk | null;
  services?: number;
  people?: number;
}

export interface Meeting {
  id: string;
  title: Bilingual;
  startsAt: string;
  endsAt: string;
  location: string | null;
  chair: Person;
  state: MeetingState;
  minutes: string | null;
  members: Person[];
  agenda: AgendaItem[];
  isChair: boolean;
  isMember: boolean;
  mayManage: boolean;
  etag: string;
  createdAt: string;
  updatedAt: string;
}

export interface ChangeBoardEntry {
  meetingId: string;
  meetingTitle: Bilingual;
  meetingStartsAt: string;
  meetingState: MeetingState;
  item: AgendaItem;
}

// ---- Task H5: the what-if simulation ----

export interface ImpactNode {
  kind: "ConfigurationItem" | "User" | "Department" | "CustomerGroup";
  id: string;
  number: string | null;
  name: string | null;
  localizedName: Bilingual | null;
  class: { id: string; name: Bilingual; family: string } | null;
}

export interface ImpactEntry {
  node: ImpactNode;
  depth: number;
}

export interface BlastRadius {
  items: ConflictItem[];
  impact: { items: ImpactEntry[]; businessServices: ImpactEntry[]; consumers: ImpactEntry[]; userCount: number; truncated: boolean };
  assetUsers: number;
  peopleCount: number;
}

export interface SimilarChange {
  id: string;
  number: string;
  title: string;
  kind: ChangeKind;
  outcome: ChangeOutcome;
  reviewNotes: string | null;
  implementedAt: string | null;
  score: number;
  reasons: ("items" | "class" | "team" | "words")[];
  sharedItems: ConflictItem[];
}

export interface RiskReason {
  cohort: "items" | "class" | "team";
  changes: number;
  failed: number;
  withProblems: number;
  failureRate: number;
}

export interface PredictedRisk {
  level: ChangeRiskLevel;
  failureChance: number;
  baseRate: number;
  history: boolean;
  reasons: RiskReason[];
  questionnaire: ChangeRiskLevel | null;
}

export interface WhatIf {
  changeId: string;
  blast: BlastRadius;
  similar: SimilarChange[];
  predicted: PredictedRisk;
  explanation: { text: string; language: string; at: string } | null;
  computedAt: string;
  kept: boolean;
}

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

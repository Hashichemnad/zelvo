/**
 * Problem management (task H1, module 16) as the API returns it: problems with their stage, the
 * root-cause tools (5 Whys, fishbone), linked incidents and articles, the timeline and the known
 * error database. The server is Zelvo.Api/Problems; keep the two equal.
 */
import type { Bilingual, Person, TicketState } from "./incidents";
import type { NamedRef, PathRef } from "./directory";
import type { SlaClock } from "./sla";

export type ProblemStage = "New" | "Assessment" | "RootCauseAnalysis" | "KnownError" | "Resolved" | "Closed";
export const problemStages: readonly ProblemStage[] = ["New", "Assessment", "RootCauseAnalysis", "KnownError", "Resolved", "Closed"];

export type ProblemTarget = "Assessment" | "RootCauseAnalysis" | "KnownError" | "OnHold" | "Resolved" | "Closed" | "Reopened";

export type KnownErrorStatus = "Active" | "Retired";

export interface WhyStep {
  why: string;
  because: string | null;
}

export interface FiveWhys {
  statement: string | null;
  steps: WhyStep[];
  conclusion: string | null;
}

export interface FishboneCause {
  category: string;
  text: string;
  root: boolean;
}

export interface Fishbone {
  effect: string | null;
  causes: FishboneCause[];
}

export interface KnownError {
  id: string;
  number: string;
  problemId: string;
  problemNumber: string;
  title: Bilingual;
  symptoms: Bilingual;
  workaround: Bilingual;
  status: KnownErrorStatus;
  articleId: string | null;
  articleNumber: string | null;
  articleState: "Draft" | "InReview" | "Published" | "Retired" | null;
  etag: string;
  createdAt: string;
  updatedAt: string;
}

export interface Problem {
  id: string;
  number: string;
  title: string;
  description: string | null;
  stage: ProblemStage;
  state: TicketState;
  phase: "Assessment" | "RootCauseAnalysis" | "KnownError";
  priority: number;
  category: PathRef | null;
  group: NamedRef | null;
  assignee: Person | null;
  createdBy: Person | null;
  rootCause: string | null;
  fiveWhys: FiveWhys | null;
  fishbone: Fishbone | null;
  knownError: KnownError | null;
  incidentCount: number;
  openIncidentCount: number;
  onHoldReason: string | null;
  resolutionNotes: string | null;
  nextTargets: ProblemTarget[];
  sla: SlaClock[];
  etag: string;
  createdAt: string;
  updatedAt: string;
  resolvedAt: string | null;
  closedAt: string | null;
}

export interface ProblemPage {
  items: Problem[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ProblemIncident {
  id: string;
  number: string;
  title: string;
  state: TicketState;
  priority: number | null;
  category: PathRef | null;
  assignee: Person | null;
  createdAt: string;
  resolvedAt: string | null;
  linkedAt: string;
  linkedBy: Person | null;
}

export interface ProblemArticle {
  id: string;
  number: string;
  state: "Draft" | "InReview" | "Published" | "Retired";
  visibility: "Internal" | "External";
  title: Bilingual | null;
  linkedAt: string;
}

export interface ProblemComment {
  id: string;
  body: string;
  language: string;
  author: Person;
  createdAt: string;
}

export interface TimelineEntry {
  at: string;
  kind: "problem.created" | "problem.known-error" | "problem.resolved" | "problem.closed" | "incident.created" | "incident.linked" | "incident.resolved" | "incident.closed"
    // Task H3: the changes that fix the problem.
    | "change.created" | "change.linked" | "change.scheduled" | "change.implemented" | "change.closed";
  ticket: { id: string; number: string; type: string; title: string };
  reference: string | null;
}

export interface MatchingKnownError {
  knownError: KnownError;
  reasons: ("problem" | "configurationItem" | "category")[];
}

export interface FishboneCategory {
  key: string;
  name: { en: string | null; ar: string | null };
  active: boolean;
}

export interface ProblemSettings {
  knownErrorPrefix: string;
  fishboneCategories: FishboneCategory[];
}

/** Most why / because steps the API accepts. */
export const maxWhySteps = 7;

/** Most fishbone causes the API accepts. */
export const maxFishboneCauses = 60;

/** The event the workspace's composer listens to: a known error's workaround pasted into the reply. */
export const composeInsertEvent = "zelvo:compose-insert";

export interface ComposeInsertDetail {
  incidentId: string;
  text: string;
  language: "en" | "ar";
}

/** Asks the composer of <paramref name="incidentId"/> to add <paramref name="text"/> to the reply. */
export function insertIntoReply(detail: ComposeInsertDetail): void {
  window.dispatchEvent(new CustomEvent<ComposeInsertDetail>(composeInsertEvent, { detail }));
}

/** Task H2: why Zelvo suggests a problem. */
export type ProblemSuggestionKind = "SimilarIncidents" | "Trend";
export type ProblemSuggestionStatus = "Open" | "Accepted" | "Dismissed";
export const problemSuggestionStatuses: readonly ProblemSuggestionStatus[] = ["Open", "Accepted", "Dismissed"];
export type ProblemTrendDimension = "Category" | "ConfigurationItem" | "Service";

export interface SuggestionIncident {
  id: string;
  number: string;
  title: string;
  state: TicketState;
  priority: number | null;
  createdAt: string;
}

/** A suggested problem (task H2): similar incidents judged by the AI, or a rising trend. */
export interface ProblemSuggestion {
  id: string;
  kind: ProblemSuggestionKind;
  status: ProblemSuggestionStatus;
  title: string | null;
  suggestedTitle: Bilingual;
  reason: string | null;
  confidence: number | null;
  language: "en" | "ar" | null;
  dimension: ProblemTrendDimension | null;
  category: PathRef | null;
  configurationItem: { id: string; number: string; name: string } | null;
  windowStart: string;
  windowEnd: string;
  incidentCount: number;
  baseline: number | null;
  incidents: SuggestionIncident[];
  problem: { id: string; number: string } | null;
  decidedBy: Person | null;
  decidedAt: string | null;
  dismissReason: string | null;
  etag: string;
  createdAt: string;
  updatedAt: string;
}

export interface ProblemSuggestionPage {
  items: ProblemSuggestion[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Settings → Problems → Problem detection (task H2). */
export interface ProblemDetectionSettings {
  similarEnabled: boolean;
  windowDays: number;
  minimumIncidents: number;
  trendsEnabled: boolean;
  trendMinimum: number;
  trendFactor: number;
  aiAvailable: boolean;
  aiReason: string | null;
}

export interface ProblemDetectionRun {
  similar: number;
  trends: number;
  updated: number;
  similarSkipped: string | null;
}

/** The title a suggestion proposes for the problem, in the page's language when there is one. */
export function suggestionTitle(s: Pick<ProblemSuggestion, "title" | "suggestedTitle">, locale: string): string {
  const preferred = locale === "ar" ? s.suggestedTitle.ar : s.suggestedTitle.en;
  return preferred ?? s.title ?? s.suggestedTitle.en ?? s.suggestedTitle.ar ?? "";
}

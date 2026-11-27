/**
 * Agent wellbeing (module 21, task K4) as the API returns it. For workload balancing, not for
 * ranking: agents come listed by name and are never sorted by their figures. See
 * docs/architecture/wellbeing.md.
 */
import type { Bilingual, TicketState } from "./incidents";

export type WellbeingAlertKind = "Workload" | "NewAssignments" | "AfterHours" | "Difficult";

export interface WellbeingThresholds {
  maxWeightedLoad: number;
  maxNewPerDay: number;
  maxAfterHoursHours: number;
  maxDifficultPercent: number;
}

export interface WellbeingSettings {
  enabled: boolean;
  enabledAt: string | null;
  staffInformed: boolean;
  defaults: WellbeingThresholds;
}

export interface WellbeingFigures {
  openAssigned: number;
  weightedLoad: number;
  newPerDay: number;
  afterHoursHours: number;
  afterHoursActions: number;
  handledTickets: number;
  difficultTickets: number;
  difficultPercent: number | null;
}

export interface WellbeingTotals {
  agents: number;
  openAssigned: number;
  weightedLoad: number;
  newPerAgentPerDay: number;
  afterHoursHours: number;
  difficultPercent: number | null;
}

export interface WellbeingAlert {
  id: string;
  groupId: string;
  kind: WellbeingAlertKind;
  value: number;
  threshold: number;
  raisedAt: string;
}

export interface WellbeingDay {
  day: string;
  openAssigned: number;
  weightedLoad: number;
  newAssignments: number;
  afterHoursHours: number;
  handledTickets: number;
  difficultTickets: number;
}

export interface WellbeingAgent {
  id: string;
  name: string;
  available: boolean;
  figures: WellbeingFigures;
  alerts: WellbeingAlert[];
  days: WellbeingDay[];
  newPerDayThisWeek: number;
}

export interface WellbeingGroupSummary {
  id: string;
  name: Bilingual;
  led: boolean;
  thresholds: WellbeingThresholds;
  totals: WellbeingTotals;
  agentsWithAlerts: number;
}

export interface WellbeingGroupDetail extends WellbeingGroupSummary {
  days: number;
  agents: WellbeingAgent[] | null;
}

export interface ReassignmentSuggestion {
  ticketId: string;
  number: string;
  title: string;
  type: string;
  state: TicketState;
  priority: number | null;
  candidates: { id: string; name: string; weightedLoad: number }[];
}

export interface MyWellbeing {
  enabled: boolean;
  enabledAt: string | null;
  days: number;
  figures: WellbeingAgent | null;
  groups: { id: string; name: Bilingual; thresholds: WellbeingThresholds }[];
}

export const wellbeingPeriods = [7, 28, 90] as const;
export const alertKinds: WellbeingAlertKind[] = ["Workload", "NewAssignments", "AfterHours", "Difficult"];

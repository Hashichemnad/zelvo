/** The AI features of task E5 (module 9): the shapes the API returns. */

import type { Bilingual } from "./incidents";

export type TriageStatus = "Pending" | "Accepted" | "Corrected" | "Dismissed" | "AutoApplied" | "Outdated";
export type AiSentiment = "Positive" | "Neutral" | "Negative" | "Frustrated";

export interface AiNamedRef {
  id: string;
  name: Bilingual;
}

export interface TriageSuggestion {
  id: string;
  status: TriageStatus;
  category: AiNamedRef | null;
  impact: number | null;
  urgency: number | null;
  group: AiNamedRef | null;
  confidence: number;
  reason: string | null;
  createdAt: string;
  decidedAt: string | null;
  final: { category: AiNamedRef | null; impact: number | null; urgency: number | null; group: AiNamedRef | null } | null;
}

export interface DuplicateCandidate {
  id: string;
  incident: { id: string; number: string; title: string; state: string };
  confidence: number;
  reason: string | null;
  status: "Pending" | "Linked" | "Dismissed";
  createdAt: string;
}

export interface AiSummary {
  text: string;
  language: "en" | "ar";
  createdAt: string;
  stale: boolean;
}

export interface AiInsights {
  /** Features that can be used now (on, with a provider, within the monthly limit). */
  available: string[];
  triage: TriageSuggestion | null;
  sentiment: AiSentiment | null;
  sentimentReason: string | null;
  sentimentAt: string | null;
  frustratedSince: string | null;
  duplicates: DuplicateCandidate[];
  summary: AiSummary | null;
}

export interface AiReply {
  text: string;
  language: "en" | "ar";
  articles: { id: string; number: string; title: string }[];
}

export interface AiTranslation {
  text: string;
  language: "en" | "ar";
  cached: boolean;
}

export interface TriageAccuracy {
  days: number;
  total: number;
  pending: number;
  accepted: number;
  corrected: number;
  dismissed: number;
  autoApplied: number;
  categoryKeptPercent: number | null;
  groupKeptPercent: number | null;
  priorityKeptPercent: number | null;
}

/** Message key (namespace ai.assist) for an AI problem code. */
export function aiErrorKey(code: string | null | undefined): string {
  switch (code) {
    case "ai-off":
    case "limit-reached":
    case "rate-limited":
      return `errors.${code}`;
    default:
      return "errors.failed";
  }
}

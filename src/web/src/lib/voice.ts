/**
 * Customer voice (module 3, task C6; GOV-2, GOV-3): complaints, inquiries and suggestions,
 * anonymous tracking, the happiness meter and the report, as the API returns them. See
 * docs/architecture/customer-voice.md.
 */
import type { Bilingual, DirectoryRef, Person, TicketChannel, TicketState } from "./incidents";
import type { SlaClock } from "./sla";

export type VoiceType = "Complaint" | "Inquiry" | "Suggestion";
export const voiceTypes: VoiceType[] = ["Complaint", "Inquiry", "Suggestion"];
export type ComplaintConfirmation = "Pending" | "Confirmed" | "Rejected" | "NotConfirmed";
export type SuggestionReview = "UnderReview" | "Accepted" | "Implemented" | "Declined";
export const suggestionReviews: SuggestionReview[] = ["UnderReview", "Accepted", "Implemented", "Declined"];

/** URL segment of each record type in the portal (`/contact/complaint`). */
export const typeSlug: Record<VoiceType, string> = { Complaint: "complaint", Inquiry: "inquiry", Suggestion: "suggestion" };

export function typeOfSlug(slug: string): VoiceType | null {
  return voiceTypes.find((t) => typeSlug[t] === slug) ?? null;
}

export interface VoiceLink {
  id: string;
  number: string;
  type: VoiceType | "ServiceRequest" | "Incident";
  state: TicketState;
}

export interface Rating {
  score: number;
  points: number;
  comment: string | null;
  createdAt: string;
}

export interface VoiceRecord {
  id: string;
  number: string;
  type: VoiceType;
  title: string;
  description: string | null;
  state: TicketState;
  channel: TicketChannel;
  requester: Person | null;
  anonymous: boolean;
  loggedBy: Person | null;
  assignee: Person | null;
  group: { id: string; name: Bilingual } | null;
  category: DirectoryRef | null;
  service: { id: string; code: string; name: Bilingual } | null;
  priority: number;
  severity: number | null;
  rootCauseKey: string | null;
  confirmation: ComplaintConfirmation | null;
  confirmationAt: string | null;
  confirmationComment: string | null;
  review: SuggestionReview | null;
  reviewFeedback: string | null;
  convertedTo: VoiceLink | null;
  convertedFrom: VoiceLink | null;
  onHoldReason: string | null;
  resolutionNotes: string | null;
  nextStates: TicketState[];
  canConfirm: boolean;
  rating: Rating | null;
  canRate: boolean;
  sla: SlaClock[];
  etag: string;
  createdAt: string;
  updatedAt: string;
  resolvedAt: string | null;
  closedAt: string | null;
}

export interface VoicePage {
  items: VoiceRecord[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface VoiceComment {
  id: string;
  visibility: "Public" | "Internal";
  body: string;
  language: string;
  author: Person | null;
  byStaff: boolean;
  createdAt: string;
}

export interface HappinessSettings {
  enabled: boolean;
  points: 3 | 5;
  question: Bilingual;
  portalQuestion: Bilingual;
  labels: Bilingual[];
  askOnClosure: boolean;
  inPortal: boolean;
}

export interface VoiceSettings {
  anonymousEnabled: boolean;
  severities: { level: number; name: Bilingual; priority: number }[];
  rootCauses: { key: string; name: Bilingual; active: boolean }[];
  inquiryPriority: number;
  suggestionPriority: number;
  happiness: HappinessSettings;
  updatedAt: string | null;
}

export interface TrackedVoice {
  record: VoiceRecord;
  comments: VoiceComment[];
}

export interface AnonymousSubmission {
  number: string;
  trackingCode: string;
  type: VoiceType;
}

export interface TicketRating {
  rating: Rating | null;
  canRate: boolean;
}

export interface VoiceReport {
  from: string;
  to: string;
  types: { type: VoiceType; total: number; open: number; resolved: number; anonymous: number; averageHoursToResolve: number | null }[];
  states: { type: VoiceType; state: TicketState; count: number }[];
  channels: { type: VoiceType; channel: TicketChannel; count: number }[];
  services: { id: string; code: string; name: Bilingual | null; complaints: number; inquiries: number; suggestions: number; ratings: number; happinessPercent: number | null }[];
  severities: { key: string; count: number }[];
  rootCauses: { key: string; count: number }[];
  confirmations: { key: string; count: number }[];
  reviews: { key: string; count: number }[];
  happiness: {
    ratings: number;
    happinessPercent: number | null;
    scores: { points: number; score: number; count: number }[];
    byRecordType: { key: string; count: number; happinessPercent: number | null }[];
    byChannel: { key: string; count: number; happinessPercent: number | null }[];
  };
  months: { month: string; complaints: number; inquiries: number; suggestions: number; ratings: number; happinessPercent: number | null }[];
}

/** Steps of a record's journey for the progress bar: received, being handled, resolved, closed. */
export type JourneyStep = "received" | "handling" | "resolved" | "closed";
export const journeySteps: JourneyStep[] = ["received", "handling", "resolved", "closed"];

export function journeyStepOf(state: TicketState): number {
  switch (state) {
    case "New":
      return 0;
    case "Assigned":
    case "InProgress":
    case "OnHold":
    case "Reopened":
      return 1;
    case "Resolved":
      return 2;
    default:
      return 3;
  }
}

/** The face shown for a score on a 3- or 5-point scale (decorative; labels carry the meaning). */
export function faceOf(score: number, points: number): string {
  const faces3 = ["☹️", "😐", "😊"];
  const faces5 = ["😠", "☹️", "😐", "🙂", "😄"];
  return (points === 5 ? faces5 : faces3)[score - 1] ?? "•";
}

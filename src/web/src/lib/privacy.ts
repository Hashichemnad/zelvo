import type { LocalizedText } from "./api";

/** Privacy and the UAE PDPL (GOV-5, task F5): shapes of the privacy API. */

export type NoticeState = "Draft" | "Published" | "Superseded";

export interface PrivacyNotice {
  id: string;
  version: number;
  title: LocalizedText;
  body: LocalizedText;
  state: NoticeState;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
}

export interface ConsentPurpose {
  id: string;
  key: string;
  name: LocalizedText;
  description: LocalizedText;
  active: boolean;
  position: number;
}

export type ConsentAction = "NoticeAccepted" | "Granted" | "Withdrawn";
export type ConsentChannel = "Web" | "Chat" | "Anonymous" | "Staff" | "Api";

export interface MyPrivacy {
  notice: { id: string; version: number; title: LocalizedText; publishedAt: string | null } | null;
  noticeAccepted: boolean;
  noticeAcceptedAt: string | null;
  purposes: { id: string; key: string; name: LocalizedText; description: LocalizedText; granted: boolean; changedAt: string | null }[];
  history: { id: string; action: ConsentAction; noticeVersion: number | null; purpose: string | null; channel: ConsentChannel; recordedByStaff: boolean; at: string }[];
}

export const ticketTypes = ["Incident", "ServiceRequest", "Complaint", "Inquiry", "Suggestion"] as const;
export type RetentionTicketType = (typeof ticketTypes)[number];

export interface PrivacySettings {
  requestDueDays: number;
  ticketRetentionDays: Partial<Record<RetentionTicketType, number>>;
  inactiveAccountDays: number | null;
}

export interface RetentionPreview {
  tickets: Partial<Record<RetentionTicketType, number>>;
  accounts: number;
}

export const requestTypes = ["Access", "Correction", "Erasure", "Restriction", "Objection"] as const;
export type RequestType = (typeof requestTypes)[number];
export type RequestState = "Received" | "InProgress" | "Completed" | "Rejected" | "Withdrawn";

export interface PrivacyRequest {
  id: string;
  number: string;
  type: RequestType;
  state: RequestState;
  channel: "Portal" | "Staff";
  details: string | null;
  receivedAt: string;
  dueAt: string;
  overdue: boolean;
  closedAt: string | null;
  resolution: string | null;
  exportedAt: string | null;
  erasedAt: string | null;
  subject: { id: string; name: string | null; email: string | null; erased: boolean; staff: boolean } | null;
  identityCheck: string | null;
  handler: string | null;
  holdings: {
    tickets: number;
    openTickets: number;
    messages: number;
    files: number;
    chats: number;
    notifications: number;
    payments: number;
    consents: number;
    erasureRefusal: "None" | "AlreadyErased" | "Self" | "LastAdministrator" | "OpenTickets";
  } | null;
}

/** Whether a request is still being handled. */
export const isOpen = (r: Pick<PrivacyRequest, "state">) => r.state === "Received" || r.state === "InProgress";

/** Retention periods accepted by the API: 30 days to 50 years. */
export const minimumDays = 30;
export const maximumDays = 365 * 50;

/** A retention field's text as days, or null for "keep"; undefined when it is not a valid period. */
export function parseDays(text: string): number | null | undefined {
  const value = text.trim();
  if (value === "") return null;
  if (!/^\d+$/.test(value)) return undefined;
  const days = Number(value);
  return days >= minimumDays && days <= maximumDays ? days : undefined;
}

/** Where a copy of a person's data is downloaded through the web proxy. */
export const exportUrl = (requestId: string | null) =>
  requestId ? `/bff/api/v1/privacy/requests/${requestId}/export` : "/bff/api/v1/me/privacy/export";

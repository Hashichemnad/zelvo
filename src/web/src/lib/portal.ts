/** The self-service portal (module 4, task D2): search-first home and "My tickets", as the API returns them. */
import type { Bilingual, TicketState } from "./incidents";

export type PortalTicketType = "Incident" | "ServiceRequest" | "Complaint" | "Inquiry" | "Suggestion";
export const portalTicketTypes: PortalTicketType[] = ["Incident", "ServiceRequest", "Complaint", "Inquiry", "Suggestion"];

export interface PortalStep {
  /** A generic step: received, handling, resolved, closed. */
  key: "received" | "handling" | "resolved" | "closed" | null;
  /** A service's own step. */
  name: Bilingual | null;
}

export interface PortalTicket {
  id: string;
  number: string;
  type: PortalTicketType;
  title: string;
  state: TicketState;
  open: boolean;
  /** The team that has it (never the person). */
  team: { id: string; name: Bilingual } | null;
  service: { id: string; code: string; name: Bilingual } | null;
  progress: { steps: PortalStep[]; current: number };
  /** The portal page, without the language. */
  path: string;
  createdAt: string;
  updatedAt: string;
}

export interface PortalTicketPage {
  items: PortalTicket[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface PortalHit {
  type: string;
  id: string;
  title: Bilingual;
  path: string;
  attributes: Record<string, string>;
}

export interface PortalSearchResult {
  services: PortalHit[];
  articles: PortalHit[];
  tickets: PortalTicket[];
}

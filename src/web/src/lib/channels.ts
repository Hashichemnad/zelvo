/** Email and live chat (module 6, task E2): types of the API's answers. */

export type MessageAuthorKind = "Requester" | "Staff" | "System";
export type MessageDelivery = "None" | "Pending" | "Sent" | "Failed";
export type TicketChannel = "Portal" | "Email" | "Chat" | "Phone" | "WhatsApp" | "Teams" | "Api" | "AiAgent";

export interface TicketMessage {
  id: string;
  authorKind: MessageAuthorKind;
  author: { id: string; displayName: string | null } | null;
  channel: TicketChannel;
  body: string;
  /** Staff only. */
  delivery?: MessageDelivery | null;
  createdAt: string;
  attachments: { id: string; fileName: string; scanStatus: string }[];
}

export type MailboxProvider = "Imap" | "Microsoft365" | "Gmail";
export type MailboxAuth = "Password" | "ClientSecret" | "ServiceAccount" | "OAuthSignIn";
export type MailSecurity = "Auto" | "SslOnConnect" | "StartTls" | "None";
export type NewSenderPolicy = "Anyone" | "AllowedDomains" | "KnownUsers";

export interface Mailbox {
  id: string;
  name: string;
  address: string;
  provider: MailboxProvider;
  auth: MailboxAuth;
  imapHost: string | null;
  imapPort: number | null;
  imapSecurity: MailSecurity;
  smtpHost: string | null;
  smtpPort: number | null;
  smtpSecurity: MailSecurity;
  username: string | null;
  clientId: string | null;
  directoryId: string | null;
  credentialSet: boolean;
  credentialSetAt: string | null;
  signedInAs: string | null;
  redirectUri: string;
  newSenders: NewSenderPolicy;
  allowedDomains: string[];
  categoryId: string | null;
  groupId: string | null;
  status: "Active" | "Disabled";
  lastCheckedAt: string | null;
  lastCheckSucceeded: boolean | null;
  lastError: string | null;
  nextCheckAt: string;
  updatedAt: string;
}

export type InboundOutcome = "Created" | "Added" | "FollowUp" | "Rejected" | "Ignored" | "Failed";

export interface InboundEmail {
  id: number;
  from: string | null;
  subject: string | null;
  outcome: InboundOutcome;
  reason: string | null;
  ticketId: string | null;
  ticketNumber: string | null;
  attempts: number;
  receivedAt: string;
}

export interface OutboundEmail {
  id: number;
  to: string;
  subject: string;
  ticketId: string;
  ticketNumber: string | null;
  reply: boolean;
  status: "Pending" | "Sent" | "Failed";
  attempts: number;
  lastError: string | null;
  createdAt: string;
  sentAt: string | null;
}

export interface Page<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface LocalizedValue {
  en: string | null;
  ar: string | null;
}

export interface ChatWidget {
  id: string;
  name: LocalizedValue;
  greeting: LocalizedValue;
  groupId: string;
  categoryId: string | null;
  allowedOrigins: string[];
  onPortal: boolean;
  active: boolean;
}

export interface PublicChatWidget {
  id: string;
  name: LocalizedValue;
  greeting: LocalizedValue;
  online: boolean;
  allowed: boolean;
  allowedOrigins: string[];
}

export type ChatState = "Waiting" | "Active" | "Ended";
export type ChatSender = "Visitor" | "Agent" | "System";

export interface ChatMessage {
  id: string;
  sequence: number;
  sender: ChatSender;
  authorName: string | null;
  body: string;
  event: string | null;
  createdAt: string;
}

export interface VisitorChat {
  id: string;
  state: ChatState;
  agentName: string | null;
  endedBy: "Visitor" | "Agent" | "System" | null;
  messages: ChatMessage[];
}

export interface AgentChat {
  id: string;
  state: ChatState;
  visitorName: string;
  visitorEmail: string;
  visitorUserId: string | null;
  language: string;
  origin: string | null;
  group: { id: string; name: LocalizedValue };
  agent: { id: string; displayName: string | null } | null;
  ticketId: string | null;
  ticketNumber: string | null;
  startedAt: string;
  acceptedAt: string | null;
  endedAt: string | null;
  endedBy: string | null;
  messages: ChatMessage[];
}

export interface ChatSummary {
  id: string;
  state: ChatState;
  visitorName: string;
  language: string;
  group: LocalizedValue;
  lastMessage: string | null;
  ticketId: string | null;
  startedAt: string;
  acceptedAt: string | null;
  endedAt: string | null;
}

export interface ChatQueue {
  waiting: ChatSummary[];
  mine: ChatSummary[];
  recent: ChatSummary[];
  limit: number;
  available: boolean;
}

/** The text of a bilingual value in <paramref name="locale"/>, else the other language. */
export function textIn(value: LocalizedValue | null | undefined, locale: string): string {
  if (!value) return "";
  return (locale === "ar" ? value.ar || value.en : value.en || value.ar) ?? "";
}

/** Merges newly fetched messages into a list, by sequence, without duplicates. */
export function mergeMessages(current: ChatMessage[], incoming: ChatMessage[]): ChatMessage[] {
  const bySequence = new Map(current.map((m) => [m.sequence, m]));
  for (const message of incoming) bySequence.set(message.sequence, message);
  return [...bySequence.values()].sort((a, b) => a.sequence - b.sequence);
}

/** The code of a stored error such as "sign-in-failed: The IMAP server refused…". */
export function errorCodeOf(error: string | null | undefined): string | null {
  if (!error) return null;
  const index = error.indexOf(":");
  return index > 0 ? error.slice(0, index) : null;
}

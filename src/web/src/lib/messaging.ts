/** WhatsApp, SMS and Microsoft Teams (module 6, task E3): the shapes the API returns. */

export type MessagingKind = "WhatsApp" | "Sms" | "Teams";
export type MessagingProvider = "WhatsAppCloud" | "Twilio" | "GenericHttp" | "TeamsBot";
export type MessagingSenders = "Anyone" | "KnownUsers";
export type GatewayAuth = "None" | "Basic" | "Bearer" | "Header";
export type GatewayBody = "Json" | "Form";

export const providersOf: Record<MessagingKind, MessagingProvider[]> = {
  WhatsApp: ["WhatsAppCloud"],
  Sms: ["Twilio", "GenericHttp"],
  Teams: ["TeamsBot"],
};

export interface GenericSmsSettings {
  url: string;
  method: "POST" | "GET";
  body: GatewayBody;
  bodyTemplate: string;
  auth: GatewayAuth;
  username: string | null;
  authHeader: string | null;
  messageIdPath: string | null;
  numbersWithoutPlus: boolean;
  inboundFrom: string;
  inboundText: string;
  inboundId: string | null;
}

export interface WhatsAppSettings {
  defaultTemplate: string | null;
  templates: Record<string, string>;
  englishCode: string;
  arabicCode: string;
}

export interface MessagingChannel {
  id: string;
  name: string;
  kind: MessagingKind;
  provider: MessagingProvider;
  accountId: string | null;
  sender: string | null;
  directoryId: string | null;
  credentialSet: boolean;
  credentialSetAt: string | null;
  appSecretSet: boolean;
  /** Where the provider sends incoming messages (register it with the provider). */
  webhookUrl: string;
  /** WhatsApp: the verify token to enter next to the webhook address in Meta's app settings. */
  verifyToken: string | null;
  gateway: GenericSmsSettings | null;
  whatsApp: WhatsAppSettings | null;
  newSenders: MessagingSenders;
  categoryId: string | null;
  groupId: string | null;
  active: boolean;
  lastInboundAt: string | null;
  lastError: string | null;
  lastErrorAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export type MessageDirection = "In" | "Out";
export type ChannelMessageStatus = "Received" | "Ignored" | "Rejected" | "Pending" | "Sent" | "Delivered" | "Read" | "Failed";

export interface ChannelMessageEntry {
  id: number;
  direction: MessageDirection;
  kind: "Text" | "Voice" | "Media" | "Template" | "Card" | "Action" | "Event";
  status: ChannelMessageStatus;
  reason: string | null;
  /** Masked. */
  address: string | null;
  textLength: number;
  ticketId: string | null;
  ticketNumber: string | null;
  attempts: number;
  createdAt: string;
  sentAt: string | null;
}

export interface MessagingTestResult {
  ok: boolean;
  detail: string | null;
  error: string | null;
  testMessageId: number | null;
}

export interface MyNotificationChannels {
  whatsApp: boolean;
  sms: boolean;
  teams: boolean;
  /** The profile's phone, masked. */
  phone: string | null;
  whatsAppOffered: boolean;
  smsOffered: boolean;
  teamsOffered: boolean;
  teamsLinked: boolean;
}

export const defaultGateway: GenericSmsSettings = {
  url: "",
  method: "POST",
  body: "Json",
  bodyTemplate: '{"to":"{{to}}","from":"{{from}}","text":"{{text}}"}',
  auth: "Bearer",
  username: null,
  authHeader: null,
  messageIdPath: null,
  numbersWithoutPlus: false,
  inboundFrom: "from",
  inboundText: "text",
  inboundId: "id",
};

/** The code of an error such as "sign-in-failed: …", for its translation. */
export function messagingErrorCode(error: string | null | undefined): string | null {
  if (!error) return null;
  const index = error.indexOf(":");
  return index > 0 ? error.slice(0, index) : error;
}

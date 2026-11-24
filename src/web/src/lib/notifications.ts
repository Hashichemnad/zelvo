import type { LocalizedText } from "./api";

/** An in-app notification (module 10, task B5), already written in the recipient's language. */
export interface AppNotification {
  id: string;
  eventType: string;
  title: string | null;
  text: string | null;
  /** "en" or "ar": the language it was written in. */
  language: "en" | "ar" | null;
  /** Page in the web app without the language, for example "/my-incidents/…". */
  linkPath: string | null;
  createdAt: string;
  readAt: string | null;
}

export type NotificationAudience = "Requester" | "Staff" | "Publisher";

/** One event under Account → Notifications. */
export interface NotificationPreference {
  eventType: string;
  audience: NotificationAudience;
  email: boolean;
  canChange: boolean;
  enabled: boolean;
  emailEnabled: boolean;
}

/** A bilingual template field as the API returns it. */
export interface TemplateField extends LocalizedText {
  missing: string[];
}

export interface NotificationTemplate {
  subject: TemplateField;
  body: TemplateField;
  inApp: TemplateField;
  action: TemplateField;
}

/** One event in Settings → Notifications. */
export interface NotificationEventSettings {
  eventType: string;
  audience: NotificationAudience;
  emailOptional: boolean;
  enabled: boolean;
  email: boolean;
  placeholders: string[];
  customised: boolean;
  template: NotificationTemplate | null;
  default: NotificationTemplate;
}

export interface NotificationSettings {
  mailServerConfigured: boolean;
  events: NotificationEventSettings[];
}

export interface NotificationPreview {
  language: "en" | "ar";
  subject: string;
  inApp: string;
  html: string;
  text: string;
}

export type DeliveryStatus = "Pending" | "Sent" | "Failed";

/** An email in the delivery log; the address is masked. */
export interface NotificationDelivery {
  id: number;
  eventType: string;
  address: string;
  language: "en" | "ar";
  subject: string;
  status: DeliveryStatus;
  attempts: number;
  manual: boolean;
  lastError: string | null;
  createdAt: string;
  sentAt: string | null;
  nextAttemptAt: string | null;
}

export interface NotificationDeliveryDetail {
  delivery: NotificationDelivery;
  address: string;
  html: string;
  text: string;
}

/** Key of an event under `notifications.events` in the translation files: "incident.awaiting-requester" → "incident_awaiting_requester". */
export function eventKey(eventType: string): string {
  return eventType.replace(/[.-]/g, "_");
}

/** Only paths inside the web app are followed; anything else is ignored. */
export function safeLinkPath(path: string | null): string | null {
  return path && /^\/[A-Za-z0-9/_?=&.-]*$/.test(path) && !path.startsWith("//") ? path : null;
}

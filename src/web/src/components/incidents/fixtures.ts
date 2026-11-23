import type { Attachment, Incident, IncidentSettings } from "@/lib/incidents";

/** Test data shaped like the API's answers. */
export const settings: IncidentSettings = {
  priorityMatrix: {
    impacts: [
      { level: 1, name: { en: "High", ar: "عالٍ" } },
      { level: 2, name: { en: "Medium", ar: "متوسط" } },
      { level: 3, name: { en: "Low", ar: "منخفض" } },
    ],
    urgencies: [
      { level: 1, name: { en: "High", ar: "عاجل" } },
      { level: 2, name: { en: "Medium", ar: "متوسط" } },
      { level: 3, name: { en: "Low", ar: "غير عاجل" } },
    ],
    priorities: [1, 2, 3, 4, 5].map((level) => ({ level, name: { en: `P${level}`, ar: null } })),
    matrix: [
      [1, 2, 3],
      [2, 3, 4],
      [3, 4, 5],
    ],
  },
  onHoldReasons: [
    { key: "awaiting-requester", name: { en: "Waiting for the requester", ar: "بانتظار ردّ مقدّم الطلب" }, active: true },
    { key: "old-reason", name: { en: "Old reason", ar: null }, active: false },
  ],
  numberPrefixes: { Incident: "INC" },
  autoCloseDays: 5,
  updatedAt: null,
};

export const incident: Incident = {
  id: "0195f000-0000-7000-8000-000000000001",
  number: "INC-000042",
  title: "Printer jams",
  description: "Paper jam on floor 2",
  state: "Assigned",
  channel: "Phone",
  requester: { id: "0195f000-0000-7000-8000-0000000000aa", displayName: "Mariam" },
  assignee: { id: "0195f000-0000-7000-8000-0000000000bb", displayName: "Hamad" },
  impact: 2,
  urgency: 2,
  priority: 3,
  isMajor: false,
  majorDeclaredAt: null,
  onHoldReason: null,
  resolutionNotes: null,
  reopenCount: 0,
  parent: null,
  followUpOf: null,
  children: [],
  nextStates: ["InProgress", "OnHold", "Resolved"],
  canReopen: false,
  createdAt: "2026-11-22T08:00:00Z",
  updatedAt: "2026-11-22T09:00:00Z",
  resolvedAt: null,
  closedAt: null,
  version: 7,
};

export const attachment = (overrides: Partial<Attachment> = {}): Attachment => ({
  id: "0195f000-0000-7000-8000-0000000000cc",
  fileName: "screenshot.png",
  contentType: "image/png",
  size: 2048,
  scanStatus: "Clean",
  downloadable: true,
  canDelete: true,
  uploadedBy: { id: "0195f000-0000-7000-8000-0000000000aa", displayName: "Mariam" },
  uploadedAt: "2026-11-22T09:30:00Z",
  ...overrides,
});

export const display = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

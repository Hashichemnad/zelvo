import type { RecordType, ReportFrequency, ReportKind, ReportPerson, ReportSchedule } from "@/lib/reports";

// Plain helpers of the schedule form, outside the client module so server pages can call them.

/** What the form starts with: an existing schedule, or a new one (perhaps from a dashboard's filter). */
export interface ScheduleFormInitial {
  id?: string;
  name: string;
  report: ReportKind;
  frequency: ReportFrequency;
  dayOfWeek: number;
  dayOfMonth: number;
  time: string;
  types: RecordType[];
  groups: string[];
  recipients: ReportPerson[];
  active: boolean;
}

/** A new schedule's starting values. */
export function newSchedule(me: ReportPerson, report: ReportKind = "Overview", types: RecordType[] = [], groups: string[] = []): ScheduleFormInitial {
  return { name: "", report, frequency: "Weekly", dayOfWeek: 1, dayOfMonth: 1, time: "07:00", types, groups, recipients: [me], active: true };
}

/** The starting values of an existing schedule. */
export function fromSchedule(s: ReportSchedule): ScheduleFormInitial {
  return {
    id: s.id, name: s.name, report: s.report, frequency: s.frequency, dayOfWeek: s.dayOfWeek ?? 1, dayOfMonth: s.dayOfMonth ?? 1, time: s.time,
    types: s.types, groups: s.groups, recipients: s.recipients, active: s.active,
  };
}


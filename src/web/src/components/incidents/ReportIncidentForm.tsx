"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, postJson } from "@/lib/client-api";
import { levelName, staffChannels, type Incident, type IncidentSettings, type Locale, type Person, type TicketChannel } from "@/lib/incidents";

export const inputClass =
  "w-full rounded-md border border-line bg-surface px-3 py-2 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";
export const buttonClass =
  "rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60";
export const secondaryButtonClass =
  "rounded-md border border-line px-3 py-1.5 text-sm font-semibold text-ink hover:bg-surface-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60";

export interface ReportIncidentFormProps {
  csrfToken: string;
  settings: IncidentSettings | null;
  /** Where the new incident opens: "/my-incidents" (requesters) or "/incidents" (staff). */
  basePath: string;
  /** Staff logging an incident for someone else: requester, channel, impact and urgency. */
  staff?: { requesters: Person[]; assignees: Person[] | null };
  /** The closed incident this one follows up. */
  followUpOf?: { id: string; number: string } | null;
}

/**
 * "Report an incident" for requesters (title and description; the portal, D2, reuses it) and
 * "Log an incident" for staff, who also choose the requester, channel, impact, urgency and assignee.
 */
export function ReportIncidentForm({ csrfToken, settings, basePath, staff, followUpOf }: ReportIncidentFormProps) {
  const t = useTranslations("incidents.form");
  const tIncidents = useTranslations("incidents");
  const tCommon = useTranslations("common");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [requesterId, setRequesterId] = useState("");
  const [channel, setChannel] = useState<TicketChannel>("Phone");
  const [impact, setImpact] = useState(2);
  const [urgency, setUrgency] = useState(2);
  const [assigneeId, setAssigneeId] = useState("");
  const [status, setStatus] = useState<"idle" | "saving" | "failed" | "invalid" | "rateLimited">("idle");
  const matrix = settings?.priorityMatrix;

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (title.trim().length < 3) {
      setStatus("invalid");
      return;
    }
    setStatus("saving");
    const body: Record<string, unknown> = { title: title.trim(), description: description.trim() || undefined, followUpOfId: followUpOf?.id };
    if (staff) {
      Object.assign(body, { requesterId: requesterId || undefined, channel, impact, urgency, assigneeId: assigneeId || undefined });
    }
    try {
      const created = await postJson<Incident>("/v1/incidents", body, csrfToken);
      router.push(`${basePath}/${created.id}`);
    } catch (error) {
      setStatus(error instanceof ApiError && error.rateLimited ? "rateLimited" : error instanceof ApiError && error.status === 400 ? "invalid" : "failed");
    }
  }

  const levelSelect = (name: "impact" | "urgency", value: number, set: (v: number) => void) => (
    <div className="space-y-1">
      <label htmlFor={`${id}-${name}`} className="block font-semibold">
        {t(name)}
      </label>
      <select id={`${id}-${name}`} value={value} onChange={(e) => set(Number(e.target.value))} className={inputClass}>
        {(name === "impact" ? matrix?.impacts : matrix?.urgencies)?.map((level) => (
          <option key={level.level} value={level.level}>
            {levelName(name === "impact" ? matrix!.impacts : matrix!.urgencies, level.level, locale)}
          </option>
        ))}
      </select>
    </div>
  );

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-5" data-testid="report-incident" noValidate>
      {followUpOf && <p className="font-semibold">{t("followUpOf", { number: followUpOf.number })}</p>}
      <div className="space-y-1">
        <label htmlFor={`${id}-title`} className="block font-semibold">
          {t("title")}
        </label>
        <input
          id={`${id}-title`}
          name="title"
          value={title}
          onChange={(e) => {
            setTitle(e.target.value);
            setStatus("idle");
          }}
          required
          minLength={3}
          maxLength={300}
          dir="auto"
          aria-describedby={`${id}-title-hint`}
          aria-invalid={status === "invalid" ? true : undefined}
          className={inputClass}
        />
        <p id={`${id}-title-hint`} className="text-sm text-ink-muted">
          {t("titleHint")}
        </p>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-description`} className="block font-semibold">
          {t("description")}
        </label>
        <textarea
          id={`${id}-description`}
          name="description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={6}
          maxLength={20000}
          dir="auto"
          aria-describedby={`${id}-description-hint`}
          className={inputClass}
        />
        <p id={`${id}-description-hint`} className="text-sm text-ink-muted">
          {t("descriptionHint")}
        </p>
      </div>
      {staff && (
        <fieldset className="grid gap-4 sm:grid-cols-2">
          <div className="space-y-1">
            <label htmlFor={`${id}-requester`} className="block font-semibold">
              {t("requester")}
            </label>
            <select id={`${id}-requester`} value={requesterId} onChange={(e) => setRequesterId(e.target.value)} className={inputClass}>
              <option value="">{t("requesterSelf")}</option>
              {staff.requesters.map((person) => (
                <option key={person.id} value={person.id}>
                  {person.displayName ?? person.id}
                </option>
              ))}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-channel`} className="block font-semibold">
              {t("channel")}
            </label>
            <select id={`${id}-channel`} value={channel} onChange={(e) => setChannel(e.target.value as TicketChannel)} className={inputClass}>
              {staffChannels.map((c) => (
                <option key={c} value={c}>
                  {tIncidents(`channels.${c}`)}
                </option>
              ))}
            </select>
          </div>
          {levelSelect("impact", impact, setImpact)}
          {levelSelect("urgency", urgency, setUrgency)}
          {matrix && (
            <p className="sm:col-span-2" aria-live="polite">
              {t("priorityPreview", { priority: levelName(matrix.priorities, matrix.matrix[impact - 1]?.[urgency - 1], locale) })}
            </p>
          )}
          {staff.assignees && (
            <div className="space-y-1">
              <label htmlFor={`${id}-assignee`} className="block font-semibold">
                {t("assignee")}
              </label>
              <select id={`${id}-assignee`} value={assigneeId} onChange={(e) => setAssigneeId(e.target.value)} className={inputClass}>
                <option value="">{t("nobody")}</option>
                {staff.assignees.map((person) => (
                  <option key={person.id} value={person.id}>
                    {person.displayName ?? person.id}
                  </option>
                ))}
              </select>
            </div>
          )}
        </fieldset>
      )}
      <div className="flex flex-wrap items-center gap-4">
        <button type="submit" disabled={status === "saving"} className={buttonClass}>
          {status === "saving" ? t("submitting") : staff ? t("submitStaff") : t("submit")}
        </button>
        <p role="status" aria-live="polite" className="text-bad">
          {status === "invalid" ? t("titleRequired") : status === "failed" ? t("failed") : status === "rateLimited" ? tCommon("rateLimited") : ""}
        </p>
      </div>
    </form>
  );
}

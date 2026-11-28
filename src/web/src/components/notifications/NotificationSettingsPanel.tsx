"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError, deleteRequest, postJson, putJson } from "@/lib/client-api";
import {
  eventKey,
  type NotificationEventSettings,
  type NotificationPreview,
  type NotificationSettings,
  type NotificationTemplate,
} from "@/lib/notifications";
import { codeClass, inputClass, primaryButton, secondaryButton } from "../settings/integrations/ui";

type Field = "subject" | "inApp" | "body" | "action";
type Draft = Record<Field, { en: string; ar: string }>;

const fields: Field[] = ["subject", "inApp", "body", "action"];

function draftOf(template: NotificationTemplate | null): Draft {
  const value = (field: Field) => ({ en: template?.[field].en ?? "", ar: template?.[field].ar ?? "" });
  return { subject: value("subject"), inApp: value("inApp"), body: value("body"), action: value("action") };
}

function problemsOf(error: unknown): string | null {
  if (error instanceof ApiError && error.status === 400) {
    const errors = (error.details.errors ?? {}) as Record<string, string[]>;
    return Object.values(errors).flat().join(" ");
  }
  return null;
}

/** Settings → Notifications → Events and wording (task B5). */
export function NotificationSettingsPanel({ initial, csrfToken }: { initial: NotificationSettings; csrfToken: string }) {
  const t = useTranslations("settings.notifications");
  const [events, setEvents] = useState(initial.events);
  const [editing, setEditing] = useState<string | null>(null);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  function replace(updated: NotificationEventSettings) {
    setEvents(events.map((e) => (e.eventType === updated.eventType ? updated : e)));
  }

  async function toggle(event: NotificationEventSettings, enabled: boolean, email: boolean, push = event.push ?? true) {
    try {
      // The other switches are sent as they are, so changing one never resets another.
      replace(
        await putJson<NotificationEventSettings>(
          `/v1/notifications/settings/events/${event.eventType}`,
          { enabled, email, messaging: event.messaging ?? true, push },
          csrfToken,
        ),
      );
      setMessage({ ok: true, text: t("eventSaved") });
    } catch {
      setMessage({ ok: false, text: t("failed") });
    }
  }

  async function testEmail() {
    try {
      await postJson("/v1/notifications/test-email", undefined, csrfToken);
      setMessage({ ok: true, text: t("testQueued") });
    } catch (error) {
      setMessage({ ok: false, text: error instanceof ApiError && error.code === "no-email-address" ? t("noEmail") : t("failed") });
    }
  }

  return (
    <div className="space-y-4" data-testid="notification-settings">
      {!initial.mailServerConfigured && (
        <p role="note" className="rounded-md border border-warn bg-surface-muted p-3" data-testid="no-mail-server">
          {t("noMailServer")}
        </p>
      )}
      {/* Task J2: web push needs outgoing internet (product owner decision 2026-11-26). */}
      <p role="note" className="rounded-md border border-line bg-surface-muted p-3 text-sm" data-testid="push-note">
        {initial.pushAvailable ? t("pushNote") : t("pushNotAvailable")}
      </p>
      <button type="button" className={secondaryButton} onClick={testEmail}>
        {t("testEmail")}
      </button>
      {message && (
        <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-bad"}>
          {message.text}
        </p>
      )}
      <ul className="space-y-3">
        {events.map((event) => (
          <EventRow
            key={event.eventType}
            event={event}
            editing={editing === event.eventType}
            onEdit={() => setEditing(editing === event.eventType ? null : event.eventType)}
            onToggle={(enabled, email, push) => void toggle(event, enabled, email, push)}
            pushAvailable={initial.pushAvailable ?? false}
            onSaved={replace}
            csrfToken={csrfToken}
          />
        ))}
      </ul>
    </div>
  );
}

function EventRow({
  event,
  editing,
  onEdit,
  onToggle,
  onSaved,
  csrfToken,
  pushAvailable,
}: {
  event: NotificationEventSettings;
  editing: boolean;
  onEdit: () => void;
  onToggle: (enabled: boolean, email: boolean, push?: boolean) => void;
  pushAvailable: boolean;
  onSaved: (event: NotificationEventSettings) => void;
  csrfToken: string;
}) {
  const t = useTranslations("settings.notifications");
  const tEvents = useTranslations("notifications.events");
  const id = useId();
  const name = tEvents(eventKey(event.eventType));

  return (
    <li className="rounded-lg border border-line bg-surface p-4" data-testid={`event-${event.eventType}`}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="font-semibold">{name}</h3>
          <p className="text-sm text-ink-muted">
            <span dir="ltr" className="font-mono">{event.eventType}</span> · {event.customised ? t("customised") : t("standard")}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-4">
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={event.enabled} onChange={(e) => onToggle(e.target.checked, event.email)} aria-describedby={`${id}-name`} />
            {t("enabled")}
          </label>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={event.email} disabled={!event.enabled} onChange={(e) => onToggle(event.enabled, e.target.checked)} aria-describedby={`${id}-name`} />
            {t("email")}
          </label>
          {pushAvailable && (
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={event.push ?? true}
                disabled={!event.enabled}
                onChange={(e) => onToggle(event.enabled, event.email, e.target.checked)}
                aria-describedby={`${id}-name`}
                data-testid={`push-${event.eventType}`}
              />
              {t("push")}
            </label>
          )}
          <span id={`${id}-name`} className="sr-only">{name}</span>
          <button type="button" className={secondaryButton} aria-expanded={editing} aria-controls={`${id}-editor`} onClick={onEdit}>
            {editing ? t("closeEditor") : t("edit")}
          </button>
        </div>
      </div>
      {!event.emailOptional && <p className="mt-1 text-sm text-ink-muted">{t("emailFixed")}</p>}
      {editing && (
        <div id={`${id}-editor`}>
          <TemplateEditor event={event} onSaved={onSaved} csrfToken={csrfToken} />
        </div>
      )}
    </li>
  );
}

function TemplateEditor({ event, onSaved, csrfToken }: { event: NotificationEventSettings; onSaved: (e: NotificationEventSettings) => void; csrfToken: string }) {
  const t = useTranslations("settings.notifications");
  const id = useId();
  const [draft, setDraft] = useState<Draft>(draftOf(event.template ?? event.default));
  const [preview, setPreview] = useState<NotificationPreview | null>(null);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  const body = () => Object.fromEntries(fields.map((f) => [f, { en: draft[f].en || null, ar: draft[f].ar || null }]));

  async function save() {
    try {
      onSaved(await putJson<NotificationEventSettings>(`/v1/notifications/templates/${event.eventType}`, body(), csrfToken));
      setMessage({ ok: true, text: t("saved") });
    } catch (error) {
      const problems = problemsOf(error);
      setMessage({ ok: false, text: problems ? t("invalid", { problems }) : t("failed") });
    }
  }

  async function reset() {
    try {
      await deleteRequest(`/v1/notifications/templates/${event.eventType}`, csrfToken);
      onSaved({ ...event, customised: false, template: null });
      setDraft(draftOf(event.default));
      setMessage({ ok: true, text: t("resetDone") });
    } catch {
      setMessage({ ok: false, text: t("failed") });
    }
  }

  async function show(language: "en" | "ar") {
    try {
      setPreview(await postJson<NotificationPreview>(`/v1/notifications/templates/${event.eventType}/preview`, { language, template: body() }, csrfToken));
      setMessage(null);
    } catch (error) {
      const problems = problemsOf(error);
      setMessage({ ok: false, text: problems ? t("invalid", { problems }) : t("failed") });
    }
  }

  return (
    <div className="mt-4 space-y-4 border-t border-line pt-4">
      <div>
        <h4 className="font-semibold">{t("placeholders")}</h4>
        <p className="text-sm text-ink-muted">{t("placeholdersHelp", { example: "{{ticket.number}}" })}</p>
        <ul className="mt-1 flex flex-wrap gap-2" dir="ltr">
          {event.placeholders.map((p) => (
            <li key={p} className="rounded bg-surface-muted px-2 py-0.5 font-mono text-sm">{`{{${p}}}`}</li>
          ))}
        </ul>
      </div>
      {fields.map((field) => (
        <fieldset key={field} className="grid gap-3 md:grid-cols-2">
          <legend className="font-semibold">{t(`fields.${field}`)}</legend>
          {(["en", "ar"] as const).map((language) => {
            const inputId = `${id}-${field}-${language}`;
            const props = {
              id: inputId,
              lang: language,
              dir: language === "ar" ? "rtl" : "ltr",
              value: draft[field][language],
              className: inputClass,
              onChange: (e: { target: { value: string } }) => setDraft({ ...draft, [field]: { ...draft[field], [language]: e.target.value } }),
            } as const;
            return (
              <div key={language}>
                <label htmlFor={inputId} className="text-sm">{t(language === "en" ? "english" : "arabic")}</label>
                {field === "body" ? <textarea rows={8} {...props} /> : <input {...props} />}
              </div>
            );
          })}
        </fieldset>
      ))}
      {message && (
        <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-bad"}>
          {message.text}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <button type="button" className={primaryButton} onClick={save}>{t("save")}</button>
        <button type="button" className={secondaryButton} onClick={() => void show("en")}>{t("previewIn", { language: t("english") })}</button>
        <button type="button" className={secondaryButton} onClick={() => void show("ar")}>{t("previewIn", { language: t("arabic") })}</button>
        {event.customised && <button type="button" className={secondaryButton} onClick={reset}>{t("reset")}</button>}
      </div>
      {preview && (
        <section aria-labelledby={`${id}-preview`} className="space-y-2" data-testid="notification-preview">
          <h4 id={`${id}-preview`} className="font-semibold">{t("previewHeading")}</h4>
          <dl lang={preview.language} dir={preview.language === "ar" ? "rtl" : "ltr"} className="space-y-1">
            <dt className="text-sm text-ink-muted">{t("previewSubject")}</dt>
            <dd data-testid="preview-subject">{preview.subject}</dd>
            <dt className="text-sm text-ink-muted">{t("previewInApp")}</dt>
            <dd>{preview.inApp}</dd>
          </dl>
          {/* The email's own HTML, isolated: no scripts, no access to this page. */}
          <iframe title={t("previewFrame")} sandbox="" srcDoc={preview.html} className="h-[32rem] w-full rounded-md border border-line bg-surface" />
          <pre className={codeClass} lang={preview.language} dir={preview.language === "ar" ? "rtl" : "ltr"}>{preview.text}</pre>
        </section>
      )}
    </div>
  );
}

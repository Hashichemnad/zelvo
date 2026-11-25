"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, getJson, patchJson, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import {
  defaultGateway,
  messagingErrorCode,
  providersOf,
  type ChannelMessageEntry,
  type GenericSmsSettings,
  type MessagingChannel,
  type MessagingKind,
  type MessagingProvider,
  type MessagingSenders,
  type MessagingTestResult,
} from "@/lib/messaging";
import type { Page } from "@/lib/channels";
import { selectableNodes, textOf, type GroupSummary, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/i18n/routing";
import { cellClass, codeClass, inputClass, primaryButton, secondaryButton, tableClass } from "../settings/integrations/ui";

const kinds: MessagingKind[] = ["WhatsApp", "Sms", "Teams"];

/**
 * Settings → Messaging (module 6, task E3): the organisation's WhatsApp number, SMS gateways and
 * Teams app. Each channel shows the webhook address to register with the provider; credentials are
 * write-only. Per channel: test, the log (numbers masked), and for Teams the app package.
 */
export function MessagingPanel({
  initial,
  csrfToken,
  categories,
  groups,
  display,
}: {
  initial: MessagingChannel[];
  csrfToken: string;
  categories: TreeNode[];
  groups: GroupSummary[];
  display: DateDisplayOptions;
}) {
  const t = useTranslations("channels.messaging");
  const [channels, setChannels] = useState(initial);
  const [adding, setAdding] = useState<MessagingKind | null>(null);
  const [open, setOpen] = useState<string | null>(null);

  const replace = (saved: MessagingChannel) => setChannels((all) => (all.some((c) => c.id === saved.id) ? all.map((c) => (c.id === saved.id ? saved : c)) : [...all, saved]));

  return (
    <div className="space-y-6" data-testid="messaging-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {channels.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("title")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("fields.name")}</th>
                <th scope="col" className={cellClass}>{t("fields.kind")}</th>
                <th scope="col" className={cellClass}>{t("fields.provider")}</th>
                <th scope="col" className={cellClass}>{t("lastInbound")}</th>
                <th scope="col" className={cellClass}><span className="sr-only">{t("open")}</span></th>
              </tr>
            </thead>
            <tbody>
              {channels.map((c) => {
                const code = messagingErrorCode(c.lastError);
                return (
                  <tr key={c.id} data-testid="messaging-row">
                    <td className={cellClass}>
                      <span className="font-semibold">{c.name}</span>
                      {!c.active && <span className="block text-sm text-ink-muted">{t("off")}</span>}
                    </td>
                    <td className={cellClass}>{t(`kinds.${c.kind}`)}</td>
                    <td className={cellClass}>{t(`providers.${c.provider}`)}</td>
                    <td className={cellClass}>
                      {c.lastInboundAt ? formatDateTime(new Date(c.lastInboundAt), display) : t("never")}
                      {code && <span className="block text-bad">{t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other")}</span>}
                    </td>
                    <td className={cellClass}>
                      <button type="button" className="text-brand underline" aria-expanded={open === c.id} onClick={() => setOpen(open === c.id ? null : c.id)}
                        aria-label={`${open === c.id ? t("close") : t("open")} ${c.name}`}>
                        {open === c.id ? t("close") : t("open")}
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {channels.filter((c) => c.id === open).map((c) => (
        <ChannelDetail key={c.id} channel={c} csrfToken={csrfToken} categories={categories} groups={groups} display={display} onSaved={replace} />
      ))}

      {adding ? (
        <section className="space-y-3 rounded-md border border-line p-4" aria-labelledby="add-channel-heading">
          <h3 id="add-channel-heading" className="text-lg font-semibold">{t(`add.${adding}`)}</h3>
          <ChannelForm channel={null} kind={adding} csrfToken={csrfToken} categories={categories} groups={groups}
            onSaved={(saved) => { replace(saved); setAdding(null); setOpen(saved.id); }} onCancel={() => setAdding(null)} />
        </section>
      ) : (
        <div className="flex flex-wrap gap-2">
          {kinds.map((kind) => (
            <button key={kind} type="button" className={primaryButton} onClick={() => setAdding(kind)}>{t(`add.${kind}`)}</button>
          ))}
        </div>
      )}
    </div>
  );
}

function ChannelDetail({
  channel,
  csrfToken,
  categories,
  groups,
  display,
  onSaved,
}: {
  channel: MessagingChannel;
  csrfToken: string;
  categories: TreeNode[];
  groups: GroupSummary[];
  display: DateDisplayOptions;
  onSaved: (saved: MessagingChannel) => void;
}) {
  const t = useTranslations("channels.messaging");
  const id = useId();
  const [testTo, setTestTo] = useState("");
  const [test, setTest] = useState<MessagingTestResult | null>(null);
  const [log, setLog] = useState<ChannelMessageEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function runTest() {
    setError(null);
    try {
      setTest(await postJson<MessagingTestResult>(`/v1/messaging/channels/${channel.id}/test`, testTo ? { to: testTo } : {}, csrfToken));
    } catch (e) {
      setError(e instanceof ApiError && e.status === 400 ? t("errors.testNumber") : t("errors.other"));
    }
  }

  async function loadLog() {
    try {
      setLog((await getJson<Page<ChannelMessageEntry>>(`/v1/messaging/channels/${channel.id}/log?pageSize=50`)).items);
    } catch {
      setError(t("errors.other"));
    }
  }

  async function resend(messageId: number) {
    await postJson(`/v1/messaging/channels/${channel.id}/log/${messageId}/resend`, {}, csrfToken);
    await loadLog();
  }

  return (
    <section className="space-y-4 rounded-md border border-line p-4" aria-labelledby={`${id}-heading`} data-testid="messaging-detail">
      <h3 id={`${id}-heading`} className="text-lg font-semibold">{channel.name}</h3>
      <div className="space-y-1">
        <p className="font-semibold">{t("webhook.title")}</p>
        <p className="text-sm text-ink-muted">{t(`webhook.${channel.provider}`)}</p>
        <code dir="ltr" className={codeClass} data-testid="webhook-url">{channel.webhookUrl}</code>
        {channel.verifyToken && (
          <>
            <p className="text-sm">{t("webhook.verifyToken")}</p>
            <code dir="ltr" className={codeClass}>{channel.verifyToken}</code>
          </>
        )}
      </div>
      {channel.kind === "Teams" && (
        <p>
          <a className="text-brand underline" href={`/bff/api/v1/messaging/channels/${channel.id}/teams-app`} download>{t("teamsApp")}</a>
          <span className="block text-sm text-ink-muted">{t("teamsAppHint")}</span>
        </p>
      )}

      <div className="flex flex-wrap items-end gap-2">
        {channel.kind !== "Teams" && (
          <label className="block">
            <span className="block text-sm">{t("testTo")}</span>
            <input dir="ltr" inputMode="tel" className={inputClass} value={testTo} onChange={(e) => setTestTo(e.target.value)} placeholder="+971501234567" />
          </label>
        )}
        <button type="button" className={secondaryButton} onClick={runTest}>{t("test")}</button>
        <button type="button" className={secondaryButton} onClick={loadLog}>{t("log.show")}</button>
      </div>
      <div aria-live="polite">
        {test && (test.ok ? <p className="text-ok">{t("testOk", { detail: test.detail ?? "" })}{test.testMessageId ? ` ${t("testQueued")}` : ""}</p>
          : <p className="text-bad">{t.has(`errors.${messagingErrorCode(test.error)}`) ? t(`errors.${messagingErrorCode(test.error)}`) : t("errors.other")}</p>)}
        {error && <p role="alert" className="text-bad">{error}</p>}
      </div>

      {log && (
        <div className="overflow-x-auto">
          <table className={tableClass} data-testid="messaging-log">
            <caption className="text-start font-semibold">{t("log.title")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("log.when")}</th>
                <th scope="col" className={cellClass}>{t("log.direction")}</th>
                <th scope="col" className={cellClass}>{t("log.who")}</th>
                <th scope="col" className={cellClass}>{t("log.status")}</th>
                <th scope="col" className={cellClass}>{t("log.ticket")}</th>
              </tr>
            </thead>
            <tbody>
              {log.length === 0 && (
                <tr><td colSpan={5} className={cellClass}>{t("log.empty")}</td></tr>
              )}
              {log.map((m) => (
                <tr key={m.id}>
                  <td className={cellClass}>{formatDateTime(new Date(m.createdAt), display)}</td>
                  <td className={cellClass}>{t(`log.directions.${m.direction}`)} · {t(`log.kinds.${m.kind}`)}</td>
                  <td className={cellClass}><span dir="ltr">{m.address ?? "—"}</span></td>
                  <td className={cellClass}>
                    {t(`log.statuses.${m.status}`)}
                    {m.reason && <span className="block text-sm text-ink-muted">{t.has(`log.reasons.${m.reason}`) ? t(`log.reasons.${m.reason}`) : m.reason}</span>}
                    {m.status === "Failed" && m.direction === "Out" && (
                      <button type="button" className="text-brand underline" onClick={() => resend(m.id)}>{t("log.resend")}</button>
                    )}
                  </td>
                  <td className={cellClass}><span dir="ltr">{m.ticketNumber ?? "—"}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <details>
        <summary className="cursor-pointer font-semibold">{t("edit")}</summary>
        <div className="pt-3">
          <ChannelForm channel={channel} kind={channel.kind} csrfToken={csrfToken} categories={categories} groups={groups} onSaved={onSaved} />
        </div>
      </details>
    </section>
  );
}

/** Adds or changes a channel; the fields follow the provider. Secrets are write-only. */
export function ChannelForm({
  channel,
  kind,
  csrfToken,
  categories,
  groups,
  onSaved,
  onCancel,
}: {
  channel: MessagingChannel | null;
  kind: MessagingKind;
  csrfToken: string;
  categories: TreeNode[];
  groups: GroupSummary[];
  onSaved: (saved: MessagingChannel) => void;
  onCancel?: () => void;
}) {
  const t = useTranslations("channels.messaging");
  const locale = useLocale() as Locale;
  const id = useId();
  const [name, setName] = useState(channel?.name ?? "");
  const [provider, setProvider] = useState<MessagingProvider>(channel?.provider ?? providersOf[kind][0]!);
  const [accountId, setAccountId] = useState(channel?.accountId ?? "");
  const [sender, setSender] = useState(channel?.sender ?? "");
  const [directoryId, setDirectoryId] = useState(channel?.directoryId ?? "");
  const [credential, setCredential] = useState("");
  const [appSecret, setAppSecret] = useState("");
  const [gateway, setGateway] = useState<GenericSmsSettings>(channel?.gateway ?? defaultGateway);
  const [defaultTemplate, setDefaultTemplate] = useState(channel?.whatsApp?.defaultTemplate ?? "");
  const [englishCode, setEnglishCode] = useState(channel?.whatsApp?.englishCode ?? "en");
  const [arabicCode, setArabicCode] = useState(channel?.whatsApp?.arabicCode ?? "ar");
  const [newSenders, setNewSenders] = useState<MessagingSenders>(channel?.newSenders ?? "Anyone");
  const [categoryId, setCategoryId] = useState(channel?.categoryId ?? "");
  const [groupId, setGroupId] = useState(channel?.groupId ?? "");
  const [active, setActive] = useState(channel?.active ?? true);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  const field = (key: string, label: string, value: string, set: (v: string) => void, props: { ltr?: boolean; type?: string; hint?: string; required?: boolean } = {}) => (
    <label className="block" htmlFor={`${id}-${key}`}>
      <span className="block font-semibold">{label}</span>
      {props.hint && <span id={`${id}-${key}-hint`} className="block text-sm text-ink-muted">{props.hint}</span>}
      <input id={`${id}-${key}`} className={inputClass} value={value} onChange={(e) => set(e.target.value)} type={props.type ?? "text"}
        dir={props.ltr ? "ltr" : undefined} required={props.required} aria-describedby={props.hint ? `${id}-${key}-hint` : undefined} autoComplete="off" />
    </label>
  );

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setErrors([]);
    setSaved(false);
    const body: Record<string, unknown> = {
      name,
      provider,
      accountId: accountId || null,
      sender: sender || null,
      directoryId,
      newSenders,
      categoryId: categoryId || "00000000-0000-0000-0000-000000000000",
      groupId: groupId || "00000000-0000-0000-0000-000000000000",
      active,
    };
    if (!channel) body.kind = kind;
    if (credential) body.credential = credential;
    if (appSecret) body.appSecret = appSecret;
    if (provider === "GenericHttp") body.gateway = gateway;
    if (provider === "WhatsAppCloud") body.whatsApp = { defaultTemplate: defaultTemplate || null, templates: channel?.whatsApp?.templates ?? {}, englishCode, arabicCode };
    try {
      const result = channel
        ? await patchJson<MessagingChannel>(`/v1/messaging/channels/${channel.id}`, body, csrfToken)
        : await postJson<MessagingChannel>("/v1/messaging/channels", body, csrfToken);
      setCredential("");
      setAppSecret("");
      setSaved(true);
      onSaved(result);
    } catch (e) {
      const problems = e instanceof ApiError ? (e.details.errors as Record<string, string[]> | undefined) : undefined;
      setErrors(problems ? Object.keys(problems).map((key) => key.replaceAll(".", "_")).map((key) => (t.has(`problems.${key}`) ? t(`problems.${key}`) : t("problems.other"))) : [t("errors.other")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-4" noValidate data-testid="messaging-form">
      {field("name", t("fields.name"), name, setName, { required: true })}
      {providersOf[kind].length > 1 && (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t("fields.provider")}</legend>
          {providersOf[kind].map((p) => (
            <label key={p} className="flex items-center gap-2">
              <input type="radio" name={`${id}-provider`} checked={provider === p} onChange={() => setProvider(p)} disabled={channel !== null} />
              {t(`providers.${p}`)}
            </label>
          ))}
        </fieldset>
      )}

      {provider === "WhatsAppCloud" && (
        <>
          {field("account", t("fields.phoneNumberId"), accountId, setAccountId, { ltr: true, hint: t("hints.phoneNumberId"), required: true })}
          {field("sender", t("fields.whatsAppNumber"), sender, setSender, { ltr: true, hint: t("hints.e164") })}
          {field("directory", t("fields.businessAccountId"), directoryId, setDirectoryId, { ltr: true })}
          {field("credential", channel?.credentialSet ? t("fields.accessTokenKeep") : t("fields.accessToken"), credential, setCredential, { ltr: true, type: "password" })}
          {field("appSecret", channel?.appSecretSet ? t("fields.appSecretKeep") : t("fields.appSecret"), appSecret, setAppSecret, { ltr: true, type: "password", hint: t("hints.appSecret") })}
          <fieldset className="space-y-2 rounded-md border border-line p-3">
            <legend className="font-semibold">{t("fields.templates")}</legend>
            <p className="text-sm text-ink-muted">{t("hints.templates")}</p>
            {field("template", t("fields.defaultTemplate"), defaultTemplate, setDefaultTemplate, { ltr: true })}
            <div className="grid gap-2 sm:grid-cols-2">
              {field("en", t("fields.englishCode"), englishCode, setEnglishCode, { ltr: true })}
              {field("ar", t("fields.arabicCode"), arabicCode, setArabicCode, { ltr: true })}
            </div>
          </fieldset>
        </>
      )}

      {provider === "Twilio" && (
        <>
          {field("account", t("fields.accountSid"), accountId, setAccountId, { ltr: true, required: true })}
          {field("sender", t("fields.smsSender"), sender, setSender, { ltr: true, hint: t("hints.smsSender"), required: true })}
          {field("credential", channel?.credentialSet ? t("fields.authTokenKeep") : t("fields.authToken"), credential, setCredential, { ltr: true, type: "password" })}
        </>
      )}

      {provider === "GenericHttp" && (
        <fieldset className="space-y-3 rounded-md border border-line p-3">
          <legend className="font-semibold">{t("gateway.title")}</legend>
          {field("url", t("gateway.url"), gateway.url, (v) => setGateway({ ...gateway, url: v }), { ltr: true, required: true })}
          <div className="grid gap-2 sm:grid-cols-2">
            <label className="block">
              <span className="block font-semibold">{t("gateway.method")}</span>
              <select className={inputClass} value={gateway.method} onChange={(e) => setGateway({ ...gateway, method: e.target.value as "POST" | "GET" })}>
                <option value="POST">POST</option>
                <option value="GET">GET</option>
              </select>
            </label>
            <label className="block">
              <span className="block font-semibold">{t("gateway.body")}</span>
              <select className={inputClass} value={gateway.body} onChange={(e) => setGateway({ ...gateway, body: e.target.value as "Json" | "Form" })}>
                <option value="Json">{t("gateway.bodies.Json")}</option>
                <option value="Form">{t("gateway.bodies.Form")}</option>
              </select>
            </label>
          </div>
          <label className="block">
            <span className="block font-semibold">{t("gateway.template")}</span>
            <span className="block text-sm text-ink-muted">{t("gateway.templateHint")}</span>
            <textarea dir="ltr" rows={4} className={`${inputClass} font-mono`} value={gateway.bodyTemplate} onChange={(e) => setGateway({ ...gateway, bodyTemplate: e.target.value })} />
          </label>
          <label className="block">
            <span className="block font-semibold">{t("gateway.auth")}</span>
            <select className={inputClass} value={gateway.auth} onChange={(e) => setGateway({ ...gateway, auth: e.target.value as GenericSmsSettings["auth"] })}>
              {(["None", "Basic", "Bearer", "Header"] as const).map((a) => <option key={a} value={a}>{t(`gateway.auths.${a}`)}</option>)}
            </select>
          </label>
          {gateway.auth === "Basic" && field("username", t("gateway.username"), gateway.username ?? "", (v) => setGateway({ ...gateway, username: v }), { ltr: true })}
          {gateway.auth === "Header" && field("authHeader", t("gateway.authHeader"), gateway.authHeader ?? "", (v) => setGateway({ ...gateway, authHeader: v }), { ltr: true })}
          {gateway.auth !== "None" && field("credential", channel?.credentialSet ? t("fields.secretKeep") : t("fields.secret"), credential, setCredential, { ltr: true, type: "password" })}
          {field("sender", t("fields.smsSender"), sender, setSender, { ltr: true, hint: t("hints.smsSender") })}
          {field("messageIdPath", t("gateway.messageIdPath"), gateway.messageIdPath ?? "", (v) => setGateway({ ...gateway, messageIdPath: v || null }), { ltr: true })}
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={gateway.numbersWithoutPlus} onChange={(e) => setGateway({ ...gateway, numbersWithoutPlus: e.target.checked })} />
            {t("gateway.withoutPlus")}
          </label>
          <p className="text-sm text-ink-muted">{t("gateway.inboundHint")}</p>
          <div className="grid gap-2 sm:grid-cols-3">
            {field("inFrom", t("gateway.inboundFrom"), gateway.inboundFrom, (v) => setGateway({ ...gateway, inboundFrom: v }), { ltr: true })}
            {field("inText", t("gateway.inboundText"), gateway.inboundText, (v) => setGateway({ ...gateway, inboundText: v }), { ltr: true })}
            {field("inId", t("gateway.inboundId"), gateway.inboundId ?? "", (v) => setGateway({ ...gateway, inboundId: v || null }), { ltr: true })}
          </div>
        </fieldset>
      )}

      {provider === "TeamsBot" && (
        <>
          {field("account", t("fields.appId"), accountId, setAccountId, { ltr: true, required: true, hint: t("hints.appId") })}
          {field("directory", t("fields.directoryId"), directoryId, setDirectoryId, { ltr: true, required: true })}
          {field("credential", channel?.credentialSet ? t("fields.clientSecretKeep") : t("fields.clientSecret"), credential, setCredential, { ltr: true, type: "password" })}
        </>
      )}

      <fieldset className="space-y-1">
        <legend className="font-semibold">{t("fields.newSenders")}</legend>
        {(["Anyone", "KnownUsers"] as const).map((policy) => (
          <label key={policy} className="flex items-center gap-2">
            <input type="radio" name={`${id}-senders`} checked={newSenders === policy} onChange={() => setNewSenders(policy)} />
            {t(`senders.${policy}`)}
          </label>
        ))}
      </fieldset>
      <div className="grid gap-2 sm:grid-cols-2">
        <label className="block">
          <span className="block font-semibold">{t("fields.category")}</span>
          <select className={inputClass} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">{t("none")}</option>
            {selectableNodes(categories).map((c) => <option key={c.id} value={c.id}>{textOf(c.path, locale)}</option>)}
          </select>
        </label>
        <label className="block">
          <span className="block font-semibold">{t("fields.group")}</span>
          <select className={inputClass} value={groupId} onChange={(e) => setGroupId(e.target.value)}>
            <option value="">{t("byRules")}</option>
            {groups.filter((g) => g.active).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
          </select>
        </label>
      </div>
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
        {t("fields.active")}
      </label>

      <div aria-live="polite">
        {errors.length > 0 && (
          <ul role="alert" className="list-disc ps-5 text-bad">{errors.map((e, i) => <li key={i}>{e}</li>)}</ul>
        )}
        {saved && <p className="text-ok">{t("saved")}</p>}
      </div>
      <div className="flex gap-2">
        <button type="submit" className={primaryButton} disabled={busy}>{t("save")}</button>
        {onCancel && <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>}
      </div>
    </form>
  );
}

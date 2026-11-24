"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, patchJson, postJson } from "@/lib/client-api";
import type { Mailbox, MailboxAuth, MailboxProvider, MailSecurity, NewSenderPolicy } from "@/lib/channels";
import { selectableNodes, textOf, type GroupSummary, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/i18n/routing";
import { inputClass, primaryButton } from "../settings/integrations/ui";

const authsOf: Record<MailboxProvider, MailboxAuth[]> = {
  Imap: ["Password"],
  Microsoft365: ["ClientSecret", "OAuthSignIn"],
  Gmail: ["ServiceAccount", "OAuthSignIn"],
};

const securities: MailSecurity[] = ["Auto", "SslOnConnect", "StartTls", "None"];

/**
 * Adds or changes a mailbox (Settings → Mailboxes, task E2). The fields follow the provider and the
 * sign-in method; the credential is write-only, and a new server or app needs it again.
 */
export function MailboxForm({
  mailbox,
  csrfToken,
  categories,
  groups,
  onSaved,
}: {
  mailbox: Mailbox | null;
  csrfToken: string;
  categories: TreeNode[];
  groups: GroupSummary[];
  onSaved: (saved: Mailbox) => void;
}) {
  const t = useTranslations("channels.mailboxes");
  const locale = useLocale() as Locale;
  const id = useId();
  const [name, setName] = useState(mailbox?.name ?? "");
  const [address, setAddress] = useState(mailbox?.address ?? "");
  const [provider, setProvider] = useState<MailboxProvider>(mailbox?.provider ?? "Imap");
  const [auth, setAuth] = useState<MailboxAuth>(mailbox?.auth ?? "Password");
  const [imapHost, setImapHost] = useState(mailbox?.imapHost ?? "");
  const [imapPort, setImapPort] = useState(String(mailbox?.imapPort ?? 993));
  const [imapSecurity, setImapSecurity] = useState<MailSecurity>(mailbox?.imapSecurity ?? "Auto");
  const [smtpHost, setSmtpHost] = useState(mailbox?.smtpHost ?? "");
  const [smtpPort, setSmtpPort] = useState(String(mailbox?.smtpPort ?? 587));
  const [smtpSecurity, setSmtpSecurity] = useState<MailSecurity>(mailbox?.smtpSecurity ?? "Auto");
  const [username, setUsername] = useState(mailbox?.username ?? "");
  const [clientId, setClientId] = useState(mailbox?.clientId ?? "");
  const [directoryId, setDirectoryId] = useState(mailbox?.directoryId ?? "");
  const [credential, setCredential] = useState("");
  const [newSenders, setNewSenders] = useState<NewSenderPolicy>(mailbox?.newSenders ?? "Anyone");
  const [domains, setDomains] = useState((mailbox?.allowedDomains ?? []).join(", "));
  const [categoryId, setCategoryId] = useState(mailbox?.categoryId ?? "");
  const [groupId, setGroupId] = useState(mailbox?.groupId ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  function changeProvider(next: MailboxProvider) {
    setProvider(next);
    if (!authsOf[next].includes(auth)) setAuth(authsOf[next][0] ?? "Password");
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setSaved(false);
    const imap = provider === "Imap";
    const body = {
      name: name.trim(),
      address: address.trim(),
      provider,
      auth,
      imapHost: imap ? imapHost.trim() : "",
      imapPort: imap ? Number(imapPort) : null,
      imapSecurity,
      smtpHost: imap ? smtpHost.trim() : "",
      smtpPort: imap ? Number(smtpPort) : null,
      smtpSecurity,
      username: imap ? username.trim() : "",
      clientId: imap ? "" : clientId.trim(),
      directoryId: provider === "Microsoft365" ? directoryId.trim() : "",
      credential: credential ? credential : undefined,
      newSenders,
      allowedDomains: domains.split(/[\s,;]+/).map((d) => d.trim()).filter(Boolean),
      categoryId: categoryId || null,
      clearCategory: !categoryId,
      groupId: groupId || null,
      clearGroup: !groupId,
    };
    try {
      const result = mailbox
        ? await patchJson<Mailbox>(`/v1/mailboxes/${mailbox.id}`, body, csrfToken)
        : await postJson<Mailbox>("/v1/mailboxes", body, csrfToken);
      setCredential("");
      setSaved(true);
      onSaved(result);
    } catch (refused) {
      const fields = refused instanceof ApiError ? Object.keys((refused.details.errors as Record<string, unknown>) ?? {}) : [];
      setError(fields.length > 0 ? t("invalid", { fields: fields.map((f) => (t.has(`fields.${f}`) ? t(`fields.${f}`) : f)).join(", ") }) : t("failed"));
    } finally {
      setBusy(false);
    }
  }

  const field = (key: string, label: string, value: string, set: (v: string) => void, options: { type?: string; ltr?: boolean; help?: string; required?: boolean } = {}) => (
    <div>
      <label htmlFor={`${id}-${key}`} className="font-semibold">{label}</label>
      <input
        id={`${id}-${key}`}
        type={options.type ?? "text"}
        dir={options.ltr ? "ltr" : undefined}
        autoComplete={options.type === "password" ? "new-password" : "off"}
        required={options.required}
        value={value}
        onChange={(e) => set(e.target.value)}
        className={inputClass}
        aria-describedby={options.help ? `${id}-${key}-help` : undefined}
      />
      {options.help && <p id={`${id}-${key}-help`} className="text-sm text-ink-muted">{options.help}</p>}
    </div>
  );

  const securitySelect = (key: string, value: MailSecurity, set: (v: MailSecurity) => void) => (
    <div>
      <label htmlFor={`${id}-${key}`} className="font-semibold">{t(`fields.${key}`)}</label>
      <select id={`${id}-${key}`} value={value} onChange={(e) => set(e.target.value as MailSecurity)} className={inputClass}>
        {securities.map((s) => <option key={s} value={s}>{t(`securities.${s}`)}</option>)}
      </select>
    </div>
  );

  const credentialLabel = t(`credentials.${auth}`);
  const credentialHelp = mailbox?.credentialSet ? t("credentialKept") : t(`credentialHelp.${auth}`);

  return (
    <form onSubmit={save} className="max-w-3xl space-y-4" noValidate data-testid="mailbox-form">
      <div className="grid gap-3 sm:grid-cols-2">
        {field("name", t("fields.name"), name, setName, { required: true })}
        {field("address", t("fields.address"), address, setAddress, { type: "email", ltr: true, required: true, help: t("addressHelp") })}
        <div>
          <label htmlFor={`${id}-provider`} className="font-semibold">{t("fields.provider")}</label>
          <select id={`${id}-provider`} value={provider} onChange={(e) => changeProvider(e.target.value as MailboxProvider)} className={inputClass}>
            {(["Imap", "Microsoft365", "Gmail"] as const).map((p) => <option key={p} value={p}>{t(`providers.${p}`)}</option>)}
          </select>
        </div>
        <div>
          <label htmlFor={`${id}-auth`} className="font-semibold">{t("fields.auth")}</label>
          <select id={`${id}-auth`} value={auth} onChange={(e) => setAuth(e.target.value as MailboxAuth)} className={inputClass}>
            {authsOf[provider].map((a) => <option key={a} value={a}>{t(`auths.${a}`)}</option>)}
          </select>
        </div>
      </div>

      {provider === "Imap" ? (
        <fieldset className="grid gap-3 rounded-md border border-line p-3 sm:grid-cols-3">
          <legend className="px-1 font-semibold">{t("serverTitle")}</legend>
          {field("imapHost", t("fields.imapHost"), imapHost, setImapHost, { ltr: true, required: true })}
          {field("imapPort", t("fields.imapPort"), imapPort, setImapPort, { type: "number", ltr: true })}
          {securitySelect("imapSecurity", imapSecurity, setImapSecurity)}
          {field("smtpHost", t("fields.smtpHost"), smtpHost, setSmtpHost, { ltr: true, required: true })}
          {field("smtpPort", t("fields.smtpPort"), smtpPort, setSmtpPort, { type: "number", ltr: true })}
          {securitySelect("smtpSecurity", smtpSecurity, setSmtpSecurity)}
          {field("username", t("fields.username"), username, setUsername, { ltr: true, required: true })}
        </fieldset>
      ) : (
        <fieldset className="grid gap-3 rounded-md border border-line p-3 sm:grid-cols-2">
          <legend className="px-1 font-semibold">{t("appTitle")}</legend>
          {auth !== "ServiceAccount" && field("clientId", t("fields.clientId"), clientId, setClientId, { ltr: true, required: true })}
          {provider === "Microsoft365" && field("directoryId", t("fields.directoryId"), directoryId, setDirectoryId, { ltr: true, help: t("directoryHelp") })}
          <p className="text-sm text-ink-muted sm:col-span-2">{t(`setupHelp.${provider}.${auth}`)}</p>
          {auth === "OAuthSignIn" && mailbox && (
            <p className="text-sm sm:col-span-2">
              {t("redirectUri")} <code dir="ltr" className="break-all">{mailbox.redirectUri}</code>
            </p>
          )}
        </fieldset>
      )}

      <div>
        <label htmlFor={`${id}-credential`} className="font-semibold">{credentialLabel}</label>
        {auth === "ServiceAccount" ? (
          <textarea id={`${id}-credential`} dir="ltr" rows={4} autoComplete="off" spellCheck={false} value={credential} onChange={(e) => setCredential(e.target.value)} className={inputClass} aria-describedby={`${id}-credential-help`} />
        ) : (
          <input id={`${id}-credential`} type="password" dir="ltr" autoComplete="new-password" value={credential} onChange={(e) => setCredential(e.target.value)} className={inputClass} aria-describedby={`${id}-credential-help`} />
        )}
        <p id={`${id}-credential-help`} className="text-sm text-ink-muted">{credentialHelp}</p>
      </div>

      <fieldset className="space-y-2 rounded-md border border-line p-3">
        <legend className="px-1 font-semibold">{t("fields.newSenders")}</legend>
        {(["Anyone", "AllowedDomains", "KnownUsers"] as const).map((policy) => (
          <div key={policy} className="flex items-start gap-2">
            <input type="radio" id={`${id}-policy-${policy}`} name={`${id}-policy`} checked={newSenders === policy} onChange={() => setNewSenders(policy)} className="mt-1" />
            <label htmlFor={`${id}-policy-${policy}`}>{t(`policies.${policy}`)}</label>
          </div>
        ))}
        {newSenders === "AllowedDomains" && field("allowedDomains", t("fields.allowedDomains"), domains, setDomains, { ltr: true, help: t("domainsHelp") })}
        <p className="text-sm text-ink-muted">{t("rejectedHelp")}</p>
      </fieldset>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label htmlFor={`${id}-category`} className="font-semibold">{t("fields.categoryId")}</label>
          <select id={`${id}-category`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass}>
            <option value="">{t("none")}</option>
            {selectableNodes(categories).map((c) => <option key={c.id} value={c.id}>{textOf(c.path, locale)}</option>)}
          </select>
        </div>
        <div>
          <label htmlFor={`${id}-group`} className="font-semibold">{t("fields.groupId")}</label>
          <select id={`${id}-group`} value={groupId} onChange={(e) => setGroupId(e.target.value)} className={inputClass} aria-describedby={`${id}-group-help`}>
            <option value="">{t("byRules")}</option>
            {groups.filter((g) => g.active).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
          </select>
          <p id={`${id}-group-help`} className="text-sm text-ink-muted">{t("groupHelp")}</p>
        </div>
      </div>

      {error && <p role="alert" className="text-bad">{error}</p>}
      {saved && <p role="status">{t("saved")}</p>}
      <button type="submit" disabled={busy || !name.trim() || !address.trim()} className={primaryButton}>{mailbox ? t("save") : t("add")}</button>
    </form>
  );
}

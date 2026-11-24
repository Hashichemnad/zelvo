"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, deleteRequest, getJson, postFile, postJson, putJson } from "@/lib/client-api";
import {
  brandingImagePath,
  brandingSlots,
  contrastChecks,
  darkSurface,
  isColour,
  lightSurface,
  type BrandingSettings,
  type BrandingSlot,
  type CustomDomain,
  type DnsRecord,
  type SenderDomain,
} from "@/lib/branding";

const input = "block w-full rounded-md border border-line bg-surface px-3 py-2";
const primary = "rounded-md bg-brand px-4 py-2 font-semibold text-surface disabled:opacity-60";
const secondary = "rounded-md border border-line px-4 py-2 font-semibold disabled:opacity-60";
const camel = (slot: BrandingSlot) => slot[0]!.toLowerCase() + slot.slice(1);

/**
 * Settings → Branding (module 11, task F1): the product name in English and Arabic, brand colours
 * for light and dark mode and a font, checked for contrast as the administrator types and shown in
 * a live preview in both languages and both modes; logos, favicon and sign-in image; publishing;
 * custom domains with their DNS records and certificates; and the email sender domain with the
 * guided SPF, DKIM and DMARC check. "Powered by Zelvo" is not a setting: it is always shown.
 */
export function BrandingPanel({
  initial,
  domains: initialDomains,
  sender: initialSender,
  tenant,
  csrfToken,
}: {
  initial: BrandingSettings;
  domains: CustomDomain[];
  sender: SenderDomain | null;
  tenant: string;
  csrfToken: string;
}) {
  const t = useTranslations("settings.branding");
  const [settings, setSettings] = useState(initial);
  return (
    <div className="space-y-10" data-testid="branding-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      <Look settings={settings} onChange={setSettings} tenant={tenant} csrfToken={csrfToken} />
      <Images settings={settings} onChange={setSettings} tenant={tenant} csrfToken={csrfToken} />
      <Publish settings={settings} onChange={setSettings} csrfToken={csrfToken} />
      <Domains initial={initialDomains} csrfToken={csrfToken} />
      <Sender initial={initialSender} csrfToken={csrfToken} />
      <p className="text-sm text-ink-muted">{t("poweredByNote")}</p>
    </div>
  );
}

function useFailure() {
  const t = useTranslations("settings.branding");
  return (error: unknown) => {
    if (error instanceof ApiError) {
      if (error.rateLimited) return t("errors.rate-limited");
      if (error.code && t.has(`errors.${error.code}`)) return t(`errors.${error.code}`);
    }
    return t("errors.failed");
  };
}

function Look({ settings, onChange, tenant, csrfToken }: { settings: BrandingSettings; onChange: (s: BrandingSettings) => void; tenant: string; csrfToken: string }) {
  const t = useTranslations("settings.branding");
  const failure = useFailure();
  const id = useId();
  const [nameEn, setNameEn] = useState(settings.draft.productName.en ?? "");
  const [nameAr, setNameAr] = useState(settings.draft.productName.ar ?? "");
  const [light, setLight] = useState(settings.draft.lightColour);
  const [dark, setDark] = useState(settings.draft.darkColour);
  const [font, setFont] = useState(settings.draft.font);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const valid = isColour(light.toLowerCase()) && isColour(dark.toLowerCase());
  const checks = valid ? contrastChecks(light.toLowerCase(), dark.toLowerCase()) : [];

  async function save(event: FormEvent) {
    event.preventDefault();
    setMessage(null);
    try {
      onChange(await putJson<BrandingSettings>("/v1/settings/branding", {
        productName: { en: nameEn.trim() || null, ar: nameAr.trim() || null },
        lightColour: light.toLowerCase(),
        darkColour: dark.toLowerCase(),
        font,
      }, csrfToken));
      setMessage({ ok: true, text: t("draftSaved") });
    } catch (error) {
      setMessage({ ok: false, text: failure(error) });
    }
  }

  const family = settings.fonts.find((f) => f.key === font)?.family ?? "IBM Plex Sans Arabic";
  const logo = brandingImagePath(tenant, "LogoLight", settings.images.logoLight?.url);
  const logoDark = brandingImagePath(tenant, "LogoDark", settings.images.logoDark?.url) ?? logo;
  return (
    <section aria-labelledby={`${id}-title`} className="space-y-4">
      <h2 id={`${id}-title`} className="text-xl font-semibold">{t("look.title")}</h2>
      <form onSubmit={save} className="grid gap-4 lg:grid-cols-2" noValidate>
        <div className="space-y-3">
          <div>
            <label htmlFor={`${id}-en`} className="font-semibold">{t("look.nameEn")}</label>
            <input id={`${id}-en`} lang="en" dir="ltr" maxLength={80} value={nameEn} onChange={(e) => setNameEn(e.target.value)} className={input} />
          </div>
          <div>
            <label htmlFor={`${id}-ar`} className="font-semibold">{t("look.nameAr")}</label>
            <input id={`${id}-ar`} lang="ar" dir="rtl" maxLength={80} value={nameAr} onChange={(e) => setNameAr(e.target.value)} className={input} />
          </div>
          {(["light", "dark"] as const).map((mode) => {
            const value = mode === "light" ? light : dark;
            const set = mode === "light" ? setLight : setDark;
            return (
              <div key={mode}>
                <label htmlFor={`${id}-${mode}`} className="font-semibold">{t(`look.${mode}Colour`)}</label>
                <div className="flex items-center gap-2">
                  <input type="color" aria-label={t(`look.${mode}Picker`)} value={isColour(value.toLowerCase()) ? value.toLowerCase() : "#000000"} onChange={(e) => set(e.target.value)} className="h-10 w-12 rounded border border-line" />
                  <input id={`${id}-${mode}`} dir="ltr" value={value} onChange={(e) => set(e.target.value.trim())} className={input} aria-describedby={`${id}-${mode}-help`} />
                </div>
                <p id={`${id}-${mode}-help`} className="text-sm text-ink-muted">{t("look.colourHelp")}</p>
              </div>
            );
          })}
          <div>
            <label htmlFor={`${id}-font`} className="font-semibold">{t("look.font")}</label>
            <select id={`${id}-font`} value={font} onChange={(e) => setFont(e.target.value)} className={input}>
              {settings.fonts.map((f) => <option key={f.key} value={f.key} style={{ fontFamily: f.family }}>{f.family}</option>)}
            </select>
          </div>
          <div aria-live="polite" data-testid="contrast-checks">
            <h3 className="font-semibold">{t("checks.title")}</h3>
            {!valid ? (
              <p className="text-bad">{t("errors.colour")}</p>
            ) : (
              <ul className="space-y-1 text-sm">
                {checks.map((c) => (
                  <li key={c.key} className={c.passed ? "text-ok" : "font-semibold text-bad"}>
                    {t(`checks.${c.key.replace(".", "_")}`)}: <span dir="ltr">{c.ratio.toFixed(2)}:1</span> — {c.passed ? t("checks.passed") : t("checks.failed")}
                  </li>
                ))}
              </ul>
            )}
          </div>
          <button type="submit" className={primary} disabled={!valid}>{t("saveDraft")}</button>
          <p aria-live="polite" className={message?.ok === false ? "text-bad" : "text-ink-muted"}>{message?.text ?? ""}</p>
        </div>
        <div className="space-y-3" aria-label={t("preview.title")} role="group">
          <h3 className="font-semibold">{t("preview.title")}</h3>
          {(["light", "dark"] as const).map((mode) => (
            <div key={mode} className="grid gap-2 sm:grid-cols-2" data-testid={`preview-${mode}`}>
              {(["en", "ar"] as const).map((language) => (
                <Preview key={language} language={language} mode={mode} colour={mode === "light" ? light : dark} family={family}
                  name={(language === "ar" ? nameAr || nameEn : nameEn || nameAr) || (language === "ar" ? "زيلفو" : "Zelvo")}
                  logo={mode === "dark" ? logoDark : logo} />
              ))}
            </div>
          ))}
        </div>
      </form>
    </section>
  );
}

function Preview({ language, mode, colour, family, name, logo }: { language: "en" | "ar"; mode: "light" | "dark"; colour: string; family: string; name: string; logo: string | null }) {
  const t = useTranslations("settings.branding.preview");
  const brand = isColour(colour.toLowerCase()) ? colour.toLowerCase() : "#000000";
  const surface = mode === "light" ? lightSurface : darkSurface;
  const ink = mode === "light" ? "#1a1f24" : "#e8edf2";
  return (
    <div dir={language === "ar" ? "rtl" : "ltr"} className="space-y-2 rounded-md border border-line p-3"
      style={{ background: surface, color: ink, fontFamily: `"${family}", sans-serif` }}>
      <p className="flex items-center gap-2 text-lg font-bold" style={{ color: brand }}>
        {/* eslint-disable-next-line @next/next/no-img-element */}
        {logo && <img src={logo} alt="" className="h-6 w-auto" />}
        <span lang={language}>{name}</span>
      </p>
      <p className="text-sm">{t("text")}</p>
      <p className="text-sm font-semibold underline" style={{ color: brand }}>{t("link")}</p>
      <span className="inline-block rounded-md px-3 py-1 text-sm font-semibold" style={{ background: brand, color: surface }}>{t("button")}</span>
    </div>
  );
}

function Images({ settings, onChange, tenant, csrfToken }: { settings: BrandingSettings; onChange: (s: BrandingSettings) => void; tenant: string; csrfToken: string }) {
  const t = useTranslations("settings.branding");
  const failure = useFailure();
  const id = useId();
  const [busy, setBusy] = useState<BrandingSlot | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  async function upload(slot: BrandingSlot, file: File | undefined) {
    if (!file) return;
    setBusy(slot);
    setMessage(null);
    try {
      onChange(await postFile<BrandingSettings>(`/v1/settings/branding/images/${slot}`, file, csrfToken));
    } catch (error) {
      setMessage(failure(error));
    } finally {
      setBusy(null);
    }
  }

  async function remove(slot: BrandingSlot) {
    setMessage(null);
    try {
      await deleteRequest(`/v1/settings/branding/images/${slot}`, csrfToken);
      onChange(await getJson<BrandingSettings>("/v1/settings/branding"));
    } catch (error) {
      setMessage(failure(error));
    }
  }

  return (
    <section aria-labelledby={`${id}-title`} className="space-y-4">
      <h2 id={`${id}-title`} className="text-xl font-semibold">{t("images.title")}</h2>
      <p className="text-sm text-ink-muted">{t("images.help")}</p>
      <ul className="grid gap-4 sm:grid-cols-2">
        {brandingSlots.map((slot) => {
          const image = settings.images[camel(slot)] ?? null;
          const src = brandingImagePath(tenant, slot, image?.url);
          return (
            <li key={slot} className="space-y-2 rounded-md border border-line p-3" data-testid={`branding-image-${slot}`}>
              <h3 className="font-semibold">{t(`images.slots.${slot}`)}</h3>
              {/* eslint-disable-next-line @next/next/no-img-element */}
              {src && image?.scanStatus !== "Pending" && <img src={src} alt={t("images.previewAlt", { name: t(`images.slots.${slot}`) })} className="max-h-24 w-auto" />}
              {image && <p className="text-sm">{t(`images.scan.${image.scanStatus}`)}</p>}
              <label className="block text-sm">
                <span className="font-semibold">{t("images.choose")}</span>
                <input type="file" accept={slot === "Favicon" ? ".png,.jpg,.jpeg,.webp,.ico" : ".png,.jpg,.jpeg,.webp"} disabled={busy !== null}
                  onChange={(e) => void upload(slot, e.target.files?.[0])} className="block text-sm" />
              </label>
              {image && <button type="button" onClick={() => void remove(slot)} className="text-sm font-semibold text-bad underline">{t("images.remove")}</button>}
            </li>
          );
        })}
      </ul>
      {message && <p role="alert" className="text-bad">{message}</p>}
    </section>
  );
}

function Publish({ settings, onChange, csrfToken }: { settings: BrandingSettings; onChange: (s: BrandingSettings) => void; csrfToken: string }) {
  const t = useTranslations("settings.branding");
  const failure = useFailure();
  const id = useId();
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  async function act(path: "publish" | "discard") {
    setMessage(null);
    try {
      onChange(await postJson<BrandingSettings>(`/v1/settings/branding/${path}`, {}, csrfToken));
      setMessage({ ok: true, text: t(path === "publish" ? "published" : "discarded") });
      if (path === "discard") window.location.reload();
    } catch (error) {
      setMessage({ ok: false, text: failure(error) });
    }
  }

  const problem = (code: string) => {
    const [kind, what, status] = code.split(":");
    return kind === "contrast" ? t("problems.contrast", { check: t(`checks.${(what ?? "").replace(".", "_")}`) }) : t("problems.image", { image: t(`images.slots.${(what ?? "")[0]?.toUpperCase()}${(what ?? "").slice(1)}`), status: status ?? "" });
  };

  return (
    <section aria-labelledby={`${id}-title`} className="space-y-3 rounded-md border border-line p-4">
      <h2 id={`${id}-title`} className="text-xl font-semibold">{t("publish.title")}</h2>
      <p>{settings.unpublished ? t("publish.unpublished") : t("publish.upToDate")}</p>
      {settings.problems.length > 0 && (
        <ul className="list-inside list-disc text-bad" data-testid="branding-problems">
          {settings.problems.map((p) => <li key={p}>{problem(p)}</li>)}
        </ul>
      )}
      <div className="flex flex-wrap gap-2">
        <button type="button" className={primary} disabled={settings.problems.length > 0 || !settings.unpublished} onClick={() => void act("publish")}>{t("publish.button")}</button>
        {settings.unpublished && <button type="button" className={secondary} onClick={() => void act("discard")}>{t("publish.discard")}</button>}
      </div>
      <p aria-live="polite" className={message?.ok === false ? "text-bad" : "text-ok"}>{message?.text ?? ""}</p>
    </section>
  );
}

function Records({ records }: { records: DnsRecord[] }) {
  const t = useTranslations("settings.branding.dns");
  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="text-start">
          <th scope="col" className="text-start">{t("type")}</th>
          <th scope="col" className="text-start">{t("name")}</th>
          <th scope="col" className="text-start">{t("value")}</th>
          <th scope="col" className="text-start">{t("status")}</th>
        </tr>
      </thead>
      <tbody>
        {records.map((r) => (
          <tr key={`${r.type}-${r.name}`} className="border-t border-line align-top">
            <td dir="ltr" className="font-mono">{r.type}</td>
            <td dir="ltr" className="break-all font-mono">{r.name}</td>
            <td dir="ltr" className="break-all font-mono">{r.value}</td>
            <td className={r.status === "Passed" ? "text-ok" : r.status === "Unknown" ? "" : "text-bad"}>{t(`states.${r.status}`)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Domains({ initial, csrfToken }: { initial: CustomDomain[]; csrfToken: string }) {
  const t = useTranslations("settings.branding.domains");
  const failure = useFailure();
  const id = useId();
  const [domains, setDomains] = useState(initial);
  const [host, setHost] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const replace = (domain: CustomDomain) => setDomains((current) => current.map((d) => (d.id === domain.id ? domain : d)));

  async function add(event: FormEvent) {
    event.preventDefault();
    setMessage(null);
    try {
      const domain = await postJson<CustomDomain>("/v1/settings/branding/domains", { host }, csrfToken);
      setDomains((current) => [...current, domain]);
      setHost("");
    } catch (error) {
      setMessage(failure(error));
    }
  }

  async function run(action: () => Promise<void>) {
    setMessage(null);
    try {
      await action();
    } catch (error) {
      setMessage(failure(error));
    }
  }

  return (
    <section aria-labelledby={`${id}-title`} className="space-y-4">
      <h2 id={`${id}-title`} className="text-xl font-semibold">{t("title")}</h2>
      <p className="max-w-3xl text-sm text-ink-muted">{t("help")}</p>
      <ul className="space-y-4">
        {domains.map((d) => (
          <li key={d.id} className="space-y-2 rounded-md border border-line p-3" data-testid="custom-domain">
            <h3 className="font-semibold" dir="ltr">{d.host}</h3>
            <p className={d.status === "Verified" ? "text-ok" : ""}>
              {t(`status.${d.status}`)}{d.lastError && d.status !== "Verified" ? ` — ${t(`errors.${d.lastError === "txt-wrong" ? "txtWrong" : "txtMissing"}`)}` : ""}
            </p>
            <Records records={d.records} />
            <p className="text-sm">{t(`certificate.${d.certificateMode}`)}</p>
            <div className="flex flex-wrap gap-2">
              <button type="button" className={secondary} onClick={() => void run(async () => replace(await postJson<CustomDomain>(`/v1/settings/branding/domains/${d.id}/verify`, {}, csrfToken)))}>
                {t("verify")}
              </button>
              {d.certificateMode === "Uploaded" && (
                <button type="button" className={secondary} onClick={() => void run(async () => {
                  await deleteRequest(`/v1/settings/branding/domains/${d.id}/certificate`, csrfToken);
                  setDomains(await getJson<CustomDomain[]>("/v1/settings/branding/domains"));
                })}>{t("certificate.useAutomatic")}</button>
              )}
              <button type="button" className={`${secondary} text-bad`} onClick={() => void run(async () => {
                await deleteRequest(`/v1/settings/branding/domains/${d.id}`, csrfToken);
                setDomains((current) => current.filter((x) => x.id !== d.id));
              })}>{t("remove")}</button>
            </div>
            <CertificateForm domain={d} csrfToken={csrfToken} onSaved={replace} />
          </li>
        ))}
      </ul>
      <form onSubmit={add} className="flex flex-wrap items-end gap-2" noValidate>
        <div>
          <label htmlFor={`${id}-host`} className="font-semibold">{t("host")}</label>
          <input id={`${id}-host`} dir="ltr" value={host} onChange={(e) => setHost(e.target.value)} placeholder="support.entity.gov.ae" className={input} />
        </div>
        <button type="submit" className={primary} disabled={!host.trim()}>{t("add")}</button>
      </form>
      {message && <p role="alert" className="text-bad">{message}</p>}
    </section>
  );
}

function CertificateForm({ domain, csrfToken, onSaved }: { domain: CustomDomain; csrfToken: string; onSaved: (d: CustomDomain) => void }) {
  const t = useTranslations("settings.branding.domains.certificate");
  const failure = useFailure();
  const id = useId();
  const [certificatePem, setCertificatePem] = useState("");
  const [privateKeyPem, setPrivateKeyPem] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  async function save(event: FormEvent) {
    event.preventDefault();
    setMessage(null);
    try {
      onSaved(await putJson<CustomDomain>(`/v1/settings/branding/domains/${domain.id}/certificate`, { certificatePem, privateKeyPem }, csrfToken));
      setCertificatePem("");
      setPrivateKeyPem("");
    } catch (error) {
      setMessage(failure(error));
    }
  }

  return (
    <details>
      <summary className="cursor-pointer text-sm font-semibold">{t("upload")}</summary>
      <form onSubmit={save} className="mt-2 space-y-2" noValidate>
        <p className="text-sm text-ink-muted">{t("help")}</p>
        <label htmlFor={`${id}-cert`} className="block font-semibold">{t("chain")}</label>
        <textarea id={`${id}-cert`} dir="ltr" rows={4} value={certificatePem} onChange={(e) => setCertificatePem(e.target.value)} className={`${input} font-mono text-xs`} />
        <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
        <textarea id={`${id}-key`} dir="ltr" rows={4} value={privateKeyPem} onChange={(e) => setPrivateKeyPem(e.target.value)} className={`${input} font-mono text-xs`} autoComplete="off" />
        <button type="submit" className={primary} disabled={!certificatePem.trim() || !privateKeyPem.trim()}>{t("save")}</button>
        {message && <p role="alert" className="text-bad">{message}</p>}
      </form>
    </details>
  );
}

function Sender({ initial, csrfToken }: { initial: SenderDomain | null; csrfToken: string }) {
  const t = useTranslations("settings.branding.sender");
  const failure = useFailure();
  const id = useId();
  const locale = useLocale();
  const [sender, setSender] = useState(initial);
  const [domain, setDomain] = useState(initial?.domain ?? "");
  const [localPart, setLocalPart] = useState(initial?.localPart ?? "noreply");
  const [message, setMessage] = useState<string | null>(null);

  async function run(action: () => Promise<void>) {
    setMessage(null);
    try {
      await action();
    } catch (error) {
      setMessage(failure(error));
    }
  }

  return (
    <section aria-labelledby={`${id}-title`} className="space-y-4">
      <h2 id={`${id}-title`} className="text-xl font-semibold">{t("title")}</h2>
      <p className="max-w-3xl text-sm text-ink-muted">{t("help")}</p>
      <form onSubmit={(e) => { e.preventDefault(); void run(async () => setSender(await putJson<SenderDomain>("/v1/settings/branding/email", { domain, localPart }, csrfToken))); }}
        className="flex flex-wrap items-end gap-2" noValidate>
        <div>
          <label htmlFor={`${id}-local`} className="font-semibold">{t("localPart")}</label>
          <input id={`${id}-local`} dir="ltr" value={localPart} onChange={(e) => setLocalPart(e.target.value)} className={input} />
        </div>
        <span aria-hidden="true" className="pb-2" dir="ltr">@</span>
        <div>
          <label htmlFor={`${id}-domain`} className="font-semibold">{t("domain")}</label>
          <input id={`${id}-domain`} dir="ltr" value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="entity.gov.ae" className={input} />
        </div>
        <button type="submit" className={primary} disabled={!domain.trim()}>{t("save")}</button>
      </form>
      {sender && (
        <div className="space-y-2" data-testid="sender-domain">
          <p className={sender.verified ? "font-semibold text-ok" : "font-semibold"}>
            {sender.verified ? t("verified", { address: sender.address }) : t("notVerified", { address: sender.address })}
          </p>
          <Records records={sender.records} />
          {sender.lastCheckedAt && <p className="text-sm text-ink-muted">{t("lastChecked", { time: new Date(sender.lastCheckedAt).toLocaleString(locale === "ar" ? "ar-AE" : "en-GB") })}</p>}
          <div className="flex flex-wrap gap-2">
            <button type="button" className={secondary} onClick={() => void run(async () => setSender(await postJson<SenderDomain>("/v1/settings/branding/email/check", {}, csrfToken)))}>{t("check")}</button>
            <button type="button" className={`${secondary} text-bad`} onClick={() => void run(async () => {
              await deleteRequest("/v1/settings/branding/email", csrfToken);
              setSender(null);
            })}>{t("remove")}</button>
          </div>
        </div>
      )}
      {message && <p role="alert" className="text-bad">{message}</p>}
    </section>
  );
}

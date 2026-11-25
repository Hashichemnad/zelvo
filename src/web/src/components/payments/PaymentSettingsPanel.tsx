"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { ApiError, deleteRequest, postJson, putJson } from "@/lib/client-api";
import {
  emptyConnector, emptyOperation, linesOf, listOf, mapOf,
  type Gateway, type GatewayConnector, type GatewayOperation, type SignatureSettings,
} from "@/lib/payments";

interface Settings {
  enabled: boolean;
  unpaidExpiryHours: number | null;
}

/** The placeholders templates may use (as the API's connector lists them). */
const placeholderList = ["amount", "amountMinor", "currency", "reference", "transactionId", "description", "returnUrl", "callbackUrl", "merchantId", "language",
  "refundAmount", "refundAmountMinor", "refundReference"].map((p) => `{{${p}}}`).join(", ");

/** Problems the API listed, as "field: message". */
function problemsOf(error: unknown): string[] {
  if (!(error instanceof ApiError)) return [];
  const problems = error.details.problems as { field: string; message: string }[] | undefined;
  return problems?.map((p) => `${p.field}: ${p.message}`) ?? [];
}

/** A signature: algorithm, credential, header, encoding and prefix. */
function SignatureEditor({ legend, value, onChange }: { legend: string; value: SignatureSettings; onChange: (s: SignatureSettings) => void }) {
  const t = useTranslations("payments.settings.connector");
  const id = useId();
  return (
    <fieldset className="grid gap-2 sm:grid-cols-3">
      <legend className="text-sm font-semibold">{legend}</legend>
      <div className="space-y-1">
        <label htmlFor={`${id}-alg`} className="block text-sm">{t("algorithm")}</label>
        <select id={`${id}-alg`} className={fieldInput} value={value.algorithm} onChange={(e) => onChange({ ...value, algorithm: e.target.value as SignatureSettings["algorithm"] })}>
          {(["None", "HmacSha256", "HmacSha512"] as const).map((a) => <option key={a} value={a}>{t(`algorithms.${a}`)}</option>)}
        </select>
      </div>
      {value.algorithm !== "None" && (
        <>
          <div className="space-y-1">
            <label htmlFor={`${id}-secret`} className="block text-sm">{t("secretName")}</label>
            <input id={`${id}-secret`} dir="ltr" className={fieldInput} value={value.secretName ?? ""} onChange={(e) => onChange({ ...value, secretName: e.target.value || null })} />
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-header`} className="block text-sm">{t("header")}</label>
            <input id={`${id}-header`} dir="ltr" className={fieldInput} value={value.header ?? ""} onChange={(e) => onChange({ ...value, header: e.target.value || null })} />
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-enc`} className="block text-sm">{t("encoding")}</label>
            <select id={`${id}-enc`} className={fieldInput} value={value.encoding} onChange={(e) => onChange({ ...value, encoding: e.target.value as SignatureSettings["encoding"] })}>
              <option value="Hex">Hex</option>
              <option value="Base64">Base64</option>
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-prefix`} className="block text-sm">{t("prefix")}</label>
            <input id={`${id}-prefix`} dir="ltr" className={fieldInput} value={value.prefix ?? ""} onChange={(e) => onChange({ ...value, prefix: e.target.value || null })} />
          </div>
        </>
      )}
    </fieldset>
  );
}

/** One call to the gateway: method, address, body format, headers, body fields and where the answer's values are. */
function OperationEditor({ legend, value, onChange }: { legend: string; value: GatewayOperation; onChange: (op: GatewayOperation) => void }) {
  const t = useTranslations("payments.settings.connector");
  const id = useId();
  const [texts, setTexts] = useState({ headers: linesOf(value.headers), body: linesOf(value.body), response: linesOf(value.response) });
  const setMap = (key: "headers" | "body" | "response", text: string) => {
    setTexts((x) => ({ ...x, [key]: text }));
    onChange({ ...value, [key]: mapOf(text) });
  };
  return (
    <fieldset className="space-y-2 rounded-md border border-line p-3" data-testid="gateway-operation">
      <legend className="font-semibold">{legend}</legend>
      <div className="grid gap-2 sm:grid-cols-4">
        <div className="space-y-1">
          <label htmlFor={`${id}-method`} className="block text-sm">{t("method")}</label>
          <select id={`${id}-method`} className={fieldInput} value={value.method} onChange={(e) => onChange({ ...value, method: e.target.value })}>
            {["GET", "POST", "PUT", "PATCH"].map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        </div>
        <div className="space-y-1 sm:col-span-2">
          <label htmlFor={`${id}-url`} className="block text-sm">{t("url")}</label>
          <input id={`${id}-url`} dir="ltr" className={fieldInput} value={value.url} onChange={(e) => onChange({ ...value, url: e.target.value })} />
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-format`} className="block text-sm">{t("bodyFormat")}</label>
          <select id={`${id}-format`} className={fieldInput} value={value.bodyFormat} onChange={(e) => onChange({ ...value, bodyFormat: e.target.value as GatewayOperation["bodyFormat"] })}>
            {(["None", "Json", "Form"] as const).map((f) => <option key={f} value={f}>{t(`formats.${f}`)}</option>)}
          </select>
        </div>
      </div>
      <div className="grid gap-2 sm:grid-cols-3">
        {(["headers", "body", "response"] as const).map((key) => (
          <div key={key} className="space-y-1">
            <label htmlFor={`${id}-${key}`} className="block text-sm">{t(key)}</label>
            <textarea id={`${id}-${key}`} rows={4} dir="ltr" className={`${fieldInput} font-mono text-sm`} value={texts[key]} aria-describedby={`${id}-lines`}
              onChange={(e) => setMap(key, e.target.value)} />
          </div>
        ))}
      </div>
      <p id={`${id}-lines`} className="text-sm text-ink-muted">{t("linesHint")}</p>
      <SignatureEditor legend={t("requestSignature")} value={value.signature} onChange={(signature) => onChange({ ...value, signature })} />
    </fieldset>
  );
}

/** A gateway's editor: name, merchant id, mode, connector mapping and credentials (write only). */
function GatewayEditor({ initial, csrfToken, onSaved, onCancel }: { initial: Gateway | null; csrfToken: string; onSaved: (g: Gateway) => void; onCancel: () => void }) {
  const t = useTranslations("payments.settings");
  const id = useId();
  const [name, setName] = useState(initial?.name ?? "");
  const [merchantId, setMerchantId] = useState(initial?.merchantId ?? "");
  const [enabled, setEnabled] = useState(initial?.enabled ?? true);
  const [isDefault, setIsDefault] = useState(initial?.isDefault ?? false);
  const [connector, setConnector] = useState<GatewayConnector>(initial?.connector ?? emptyConnector());
  const [credentials, setCredentials] = useState<{ name: string; value: string }[]>(
    (initial?.credentialNames ?? ["apiKey"]).map((n) => ({ name: n, value: "" })));
  const [problems, setProblems] = useState<string[]>([]);
  const [status, setStatus] = useState("");
  const set = (patch: Partial<GatewayConnector>) => setConnector((c) => ({ ...c, ...patch }));

  async function save() {
    setProblems([]);
    setStatus(t("saving"));
    const given = Object.fromEntries(credentials.filter((c) => c.name.trim() && c.value).map((c) => [c.name.trim(), c.value]));
    const body = { name, merchantId, enabled, isDefault, connector, credentials: given,
      removeCredentials: (initial?.credentialNames ?? []).filter((n) => !credentials.some((c) => c.name.trim() === n)) };
    try {
      const saved = initial
        ? await putJson<Gateway>(`/v1/payments/gateways/${initial.id}`, body, csrfToken)
        : await postJson<Gateway>("/v1/payments/gateways", body, csrfToken);
      setStatus(t("saved"));
      onSaved(saved);
    } catch (error) {
      setProblems(problemsOf(error));
      setStatus(error instanceof ApiError && error.status === 400 ? t("invalid") : t("failed"));
    }
  }

  return (
    <form className="space-y-4 rounded-lg border border-line p-4" onSubmit={(e) => { e.preventDefault(); void save(); }} data-testid="gateway-editor">
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-name`} className="block font-semibold">{t("name")}</label>
          <input id={`${id}-name`} required maxLength={100} className={fieldInput} value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-merchant`} className="block font-semibold">{t("merchantId")}</label>
          <input id={`${id}-merchant`} dir="ltr" maxLength={200} className={fieldInput} value={merchantId} onChange={(e) => setMerchantId(e.target.value)} />
        </div>
      </div>
      <div className="flex flex-wrap gap-4">
        <label className="flex items-center gap-2"><input type="checkbox" className="size-6" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />{t("enabled")}</label>
        <label className="flex items-center gap-2"><input type="checkbox" className="size-6" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} />{t("isDefault")}</label>
      </div>
      <p className="text-sm text-ink-muted">{t("placeholdersHint", { list: placeholderList, secret: "{{secret.NAME}}" })}</p>
      <OperationEditor legend={t("create")} value={connector.create} onChange={(create) => set({ create })} />
      <OperationEditor legend={t("status")} value={connector.status} onChange={(status) => set({ status })} />
      <label className="flex items-center gap-2">
        <input type="checkbox" className="size-6" checked={connector.refund !== null}
          onChange={(e) => set({ refund: e.target.checked ? { ...emptyOperation("POST", "Json"), response: { refundId: "", status: "" } } : null })} />
        {t("hasRefund")}
      </label>
      {connector.refund && <OperationEditor legend={t("refund")} value={connector.refund} onChange={(refund) => set({ refund })} />}
      <fieldset className="grid gap-2 sm:grid-cols-3">
        <legend className="font-semibold">{t("statusValues")}</legend>
        {(["paid", "failed", "cancelled"] as const).map((k) => (
          <div key={k} className="space-y-1">
            <label htmlFor={`${id}-sv-${k}`} className="block text-sm">{t(`statuses.${k}`)}</label>
            <input id={`${id}-sv-${k}`} dir="ltr" className={fieldInput} defaultValue={connector.statusValues[k].join(", ")}
              onChange={(e) => set({ statusValues: { ...connector.statusValues, [k]: listOf(e.target.value) } })} />
          </div>
        ))}
      </fieldset>
      <label className="flex items-center gap-2">
        <input type="checkbox" className="size-6" checked={connector.statusAmountInMinorUnits} onChange={(e) => set({ statusAmountInMinorUnits: e.target.checked })} />
        {t("minorUnits")}
      </label>
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("callback")}</legend>
        <div className="max-w-sm space-y-1">
          <label htmlFor={`${id}-cbref`} className="block text-sm">{t("referenceField")}</label>
          <input id={`${id}-cbref`} dir="ltr" className={fieldInput} value={connector.callback.referenceField}
            onChange={(e) => set({ callback: { ...connector.callback, referenceField: e.target.value } })} />
        </div>
        <SignatureEditor legend={t("callbackSignature")} value={connector.callback.signature} onChange={(signature) => set({ callback: { ...connector.callback, signature } })} />
      </fieldset>
      <fieldset className="grid gap-2 sm:grid-cols-3">
        <legend className="font-semibold">{t("settlement")}</legend>
        {(["reference", "transactionId", "amount"] as const).map((k) => (
          <div key={k} className="space-y-1">
            <label htmlFor={`${id}-st-${k}`} className="block text-sm">{t(`settlementColumns.${k}`)}</label>
            <input id={`${id}-st-${k}`} className={fieldInput} value={connector.settlement?.[k] ?? ""}
              onChange={(e) => set({ settlement: { reference: "", transactionId: null, amount: "", status: null, amountInMinorUnits: false, ...connector.settlement, [k]: e.target.value || (k === "transactionId" ? null : "") } })} />
          </div>
        ))}
      </fieldset>
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("credentials")}</legend>
        <p className="text-sm text-ink-muted">{t("credentialsHint")}</p>
        {credentials.map((c, i) => (
          <div key={i} className="flex flex-wrap items-end gap-2" data-testid="gateway-credential">
            <div className="space-y-1">
              <label htmlFor={`${id}-cn-${i}`} className="block text-sm">{t("credentialName", { number: i + 1 })}</label>
              <input id={`${id}-cn-${i}`} dir="ltr" className={fieldInput} value={c.name} onChange={(e) => setCredentials((list) => list.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-cv-${i}`} className="block text-sm">{t("credentialValue", { number: i + 1 })}</label>
              <input id={`${id}-cv-${i}`} type="password" autoComplete="off" dir="ltr" className={fieldInput} value={c.value}
                placeholder={initial?.credentialNames.includes(c.name) ? t("kept") : ""}
                onChange={(e) => setCredentials((list) => list.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))} />
            </div>
            <button type="button" className={fieldButton} onClick={() => setCredentials((list) => list.filter((_, j) => j !== i))} aria-label={t("removeCredential", { number: i + 1 })}>{t("remove")}</button>
          </div>
        ))}
        <button type="button" className={fieldButton} onClick={() => setCredentials((list) => [...list, { name: "", value: "" }])}>{t("addCredential")}</button>
      </fieldset>
      {problems.length > 0 && <ul role="alert" className="list-inside list-disc text-bad" dir="ltr">{problems.map((p) => <li key={p}>{p}</li>)}</ul>}
      <div className="flex flex-wrap items-center gap-3">
        <button type="submit" className={primaryAction}>{t("save")}</button>
        <button type="button" className={fieldButton} onClick={onCancel}>{t("cancel")}</button>
        <p role="status" aria-live="polite">{status}</p>
      </div>
    </form>
  );
}

/**
 * Settings → Payments (task E8, module 15): online payments on or off (off: fees are for
 * information only), how long unpaid requests wait, and the gateways: connected through the one
 * generic connector, tested, paid once in sandbox mode, then live.
 */
export function PaymentSettingsPanel({ initialSettings, initialGateways, csrfToken }: { initialSettings: Settings; initialGateways: Gateway[]; csrfToken: string }) {
  const t = useTranslations("payments.settings");
  const id = useId();
  const [settings, setSettings] = useState(initialSettings);
  const [hours, setHours] = useState(String(initialSettings.unpaidExpiryHours ?? 72));
  const [gateways, setGateways] = useState(initialGateways);
  const [editing, setEditing] = useState<Gateway | "new" | null>(null);
  const [status, setStatus] = useState("");

  async function saveSettings(enabled: boolean) {
    try {
      setSettings(await putJson<Settings>("/v1/settings/payments", { enabled, unpaidExpiryHours: Number(hours) }, csrfToken));
      setStatus(t("saved"));
    } catch (error) {
      setStatus(error instanceof ApiError && error.code === "no-gateway" ? t("noGateway") : t("failed"));
    }
  }

  async function action(gateway: Gateway, kind: "test" | "pay" | "live" | "test-mode" | "delete") {
    try {
      if (kind === "test") {
        const result = await postJson<{ reachable: boolean; statusCode: number; error: string | null; milliseconds: number }>(`/v1/payments/gateways/${gateway.id}/test-connection`, undefined, csrfToken);
        setStatus(result.reachable ? t("reachable", { code: result.statusCode, ms: result.milliseconds }) : t("unreachable", { error: result.error ?? "" }));
      } else if (kind === "pay") {
        const { redirectUrl } = await postJson<{ redirectUrl: string }>(`/v1/payments/gateways/${gateway.id}/test-payment`, undefined, csrfToken);
        window.open(redirectUrl, "_blank", "noopener");
        setStatus(t("testPaymentOpened"));
      } else if (kind === "delete") {
        await deleteRequest(`/v1/payments/gateways/${gateway.id}`, csrfToken);
        setGateways((list) => list.filter((g) => g.id !== gateway.id));
      } else {
        const saved = await putJson<Gateway>(`/v1/payments/gateways/${gateway.id}`, { mode: kind === "live" ? "Live" : "Test" }, csrfToken);
        setGateways((list) => list.map((g) => (g.id === saved.id ? saved : g)));
      }
    } catch (error) {
      setStatus(error instanceof ApiError && t.has(`errors.${error.code}`) ? t(`errors.${error.code}`) : t("failed"));
    }
  }

  return (
    <div className="space-y-6" data-testid="payment-settings">
      <section aria-labelledby={`${id}-switch`} className="space-y-3 rounded-lg border border-line p-4">
        <h3 id={`${id}-switch`} className="text-lg font-semibold">{t("switchTitle")}</h3>
        <p>{settings.enabled ? t("on") : t("off")}</p>
        <div className="max-w-xs space-y-1">
          <label htmlFor={`${id}-hours`} className="block text-sm font-semibold">{t("unpaidExpiryHours")}</label>
          <input id={`${id}-hours`} type="number" min={1} max={2160} dir="ltr" className={fieldInput} value={hours} onChange={(e) => setHours(e.target.value)} />
        </div>
        <div className="flex flex-wrap gap-2">
          <button type="button" className={primaryAction} onClick={() => void saveSettings(!settings.enabled)}>{settings.enabled ? t("turnOff") : t("turnOn")}</button>
          {settings.enabled && <button type="button" className={fieldButton} onClick={() => void saveSettings(true)}>{t("saveHours")}</button>}
        </div>
      </section>
      <section aria-labelledby={`${id}-gateways`} className="space-y-3">
        <h3 id={`${id}-gateways`} className="text-lg font-semibold">{t("gateways")}</h3>
        <p className="text-sm text-ink-muted">{t("gatewaysHint")}</p>
        {gateways.length === 0 && <p>{t("noGateways")}</p>}
        <ul className="space-y-2">
          {gateways.map((g) => (
            <li key={g.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-line p-3" data-testid="gateway">
              <div>
                <p className="font-semibold">{g.name}{g.isDefault && <span className="ms-2 text-sm font-normal">({t("default")})</span>}</p>
                <p className="text-sm">{t(`modes.${g.mode}`)} · {g.enabled ? t("enabled") : t("disabled")}{g.testPaymentSucceededAt ? ` · ${t("testPassed")}` : ""}</p>
              </div>
              <div className="flex flex-wrap gap-2">
                <button type="button" className={fieldButton} onClick={() => setEditing(g)}>{t("edit")}</button>
                <button type="button" className={fieldButton} onClick={() => void action(g, "test")}>{t("testConnection")}</button>
                {g.mode === "Test" && <button type="button" className={fieldButton} onClick={() => void action(g, "pay")}>{t("testPayment")}</button>}
                {g.mode === "Test" ? (
                  <button type="button" className={fieldButton} disabled={!g.testPaymentSucceededAt} onClick={() => void action(g, "live")}>{t("goLive")}</button>
                ) : (
                  <button type="button" className={fieldButton} onClick={() => void action(g, "test-mode")}>{t("backToTest")}</button>
                )}
                <button type="button" className={fieldButton} onClick={() => void action(g, "delete")}>{t("delete")}</button>
              </div>
            </li>
          ))}
        </ul>
        {editing ? (
          <GatewayEditor key={editing === "new" ? "new" : editing.id} initial={editing === "new" ? null : editing} csrfToken={csrfToken} onCancel={() => setEditing(null)}
            onSaved={(saved) => {
              setGateways((list) => [...list.filter((g) => g.id !== saved.id).map((g) => (saved.isDefault ? { ...g, isDefault: false } : g)), saved]);
              setEditing(null);
            }} />
        ) : (
          <button type="button" className={fieldButton} onClick={() => setEditing("new")}>{t("addGateway")}</button>
        )}
      </section>
      <p role="status" aria-live="polite">{status}</p>
    </div>
  );
}

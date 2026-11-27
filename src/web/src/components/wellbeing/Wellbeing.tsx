"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { inputClass, primaryButton, secondaryButton, smallButton } from "@/components/directory/common";
import { getJson, postJson, putJson } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import { formatNumber, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import {
  wellbeingPeriods,
  type MyWellbeing,
  type ReassignmentSuggestion,
  type WellbeingAgent,
  type WellbeingAlert,
  type WellbeingFigures,
  type WellbeingGroupDetail,
  type WellbeingGroupSummary,
  type WellbeingSettings,
  type WellbeingThresholds,
  type WellbeingTotals,
} from "@/lib/wellbeing";

type Display = Pick<DateDisplayOptions, "locale" | "digits">;

function useNumber(display: Display) {
  return (value: number | null | undefined) => (value == null ? "—" : formatNumber(value, display.locale, display.digits));
}

/** An agent's figures as a description list. */
function Figures({ figures, days, display }: { figures: WellbeingFigures; days: number; display: Display }) {
  const t = useTranslations("wellbeing.figures");
  const n = useNumber(display);
  const rows: [string, string][] = [
    [t("open"), t("openValue", { open: n(figures.openAssigned), weighted: n(figures.weightedLoad) })],
    [t("newPerDay", { days }), n(figures.newPerDay)],
    [t("afterHours", { days }), t("afterHoursValue", { hours: n(figures.afterHoursHours), actions: n(figures.afterHoursActions) })],
    [t("difficult", { days }), figures.difficultPercent == null ? t("none") : t("difficultValue", { percent: n(figures.difficultPercent), difficult: n(figures.difficultTickets), handled: n(figures.handledTickets) })],
  ];
  return (
    <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-[auto_1fr]">
      {rows.map(([label, value]) => (
        <div key={label} className="contents">
          <dt className="font-semibold">{label}</dt>
          <dd>{value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Open alerts, said in words. */
function Alerts({ alerts, display }: { alerts: WellbeingAlert[]; display: Display }) {
  const t = useTranslations("wellbeing.alerts");
  const n = useNumber(display);
  if (alerts.length === 0) return <p className="text-ink-muted">{t("none")}</p>;
  return (
    <ul className="list-disc space-y-1 ps-5" data-testid="wellbeing-alerts">
      {alerts.map((a) => <li key={a.id}>{t(`kinds.${a.kind}`, { value: n(a.value), threshold: n(a.threshold) })}</li>)}
    </ul>
  );
}

/** Group totals: never per person. */
function Totals({ totals, display }: { totals: WellbeingTotals; display: Display }) {
  const t = useTranslations("wellbeing.totals");
  const n = useNumber(display);
  return (
    <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-[auto_1fr]" data-testid="wellbeing-totals">
      <dt className="font-semibold">{t("agents")}</dt><dd>{n(totals.agents)}</dd>
      <dt className="font-semibold">{t("open")}</dt><dd>{t("openValue", { open: n(totals.openAssigned), weighted: n(totals.weightedLoad) })}</dd>
      <dt className="font-semibold">{t("newPerAgent")}</dt><dd>{n(totals.newPerAgentPerDay)}</dd>
      <dt className="font-semibold">{t("afterHours")}</dt><dd>{n(totals.afterHoursHours)}</dd>
      <dt className="font-semibold">{t("difficult")}</dt><dd>{totals.difficultPercent == null ? "—" : `${n(totals.difficultPercent)} %`}</dd>
    </dl>
  );
}

/** Four thresholds with labels; empty group values take the defaults. */
function ThresholdsEditor({ initial, onSave, busy, legend }: {
  initial: WellbeingThresholds;
  onSave: (t: WellbeingThresholds) => void;
  busy: boolean;
  legend: string;
}) {
  const t = useTranslations("wellbeing.thresholds");
  const id = useId();
  const [values, setValues] = useState(initial);
  const fields: [keyof WellbeingThresholds, number, number][] = [
    ["maxWeightedLoad", 1, 1000],
    ["maxNewPerDay", 1, 500],
    ["maxAfterHoursHours", 0, 168],
    ["maxDifficultPercent", 1, 100],
  ];
  return (
    <form className="space-y-2" onSubmit={(e) => { e.preventDefault(); onSave(values); }}>
      <fieldset className="grid gap-2 sm:grid-cols-2">
        <legend className="font-semibold">{legend}</legend>
        {fields.map(([key, min, max]) => (
          <div key={key} className="space-y-1">
            <label htmlFor={`${id}-${key}`} className="block text-sm">{t(key)}</label>
            <input id={`${id}-${key}`} type="number" dir="ltr" min={min} max={max} required className={inputClass} value={values[key]}
              onChange={(e) => setValues({ ...values, [key]: Number(e.target.value) })} />
          </div>
        ))}
      </fieldset>
      <button type="submit" className={secondaryButton} disabled={busy}>{t("save")}</button>
    </form>
  );
}

/** Suggested reassignment for one agent: the team lead picks; nothing moves on its own. */
function Suggestions({ groupId, agent, csrfToken, onMoved }: { groupId: string; agent: WellbeingAgent; csrfToken: string; onMoved: () => void }) {
  const t = useTranslations("wellbeing.suggestions");
  const [items, setItems] = useState<ReassignmentSuggestion[] | null>(null);
  const [status, setStatus] = useState("");
  useEffect(() => {
    let live = true;
    getJson<ReassignmentSuggestion[]>(`/v1/wellbeing/groups/${groupId}/agents/${agent.id}/suggestions`)
      .then((s) => { if (live) setItems(s); })
      .catch(() => { if (live) setItems([]); });
    return () => { live = false; };
  }, [groupId, agent.id]);

  async function move(ticketId: string, toUserId: string, name: string, number: string) {
    try {
      await postJson(`/v1/wellbeing/reassign`, { groupId, ticketId, toUserId }, csrfToken);
      setItems((list) => list?.filter((i) => i.ticketId !== ticketId) ?? null);
      setStatus(t("moved", { number, name }));
      onMoved();
    } catch {
      setStatus(t("failed"));
    }
  }

  return (
    <div className="space-y-2" data-testid="wellbeing-suggestions">
      <p className="text-sm text-ink-muted">{t("intro")}</p>
      {items === null ? <p>{t("loading")}</p> : items.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-2">
          {items.map((s) => (
            <li key={s.ticketId} className="space-y-1 rounded-md border border-line p-2">
              <p><span dir="ltr">{s.number}</span> · {s.title}{s.priority ? ` · P${s.priority}` : ""}</p>
              {s.candidates.length === 0 ? <p className="text-sm text-ink-muted">{t("nobody")}</p> : (
                <div className="flex flex-wrap gap-2">
                  {s.candidates.map((c) => (
                    <button key={c.id} type="button" className={smallButton} onClick={() => void move(s.ticketId, c.id, c.name, s.number)}>
                      {t("give", { number: s.number, name: c.name })}
                    </button>
                  ))}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      <p aria-live="polite">{status}</p>
    </div>
  );
}

/**
 * The wellbeing dashboard (module 21, task K4): for team leads, each agent of the groups they lead,
 * listed by name (never ranked), with alerts and suggested reassignment; for others with access,
 * group totals only.
 */
export function WellbeingDashboard({ groups, csrfToken, display }: { groups: WellbeingGroupSummary[]; csrfToken: string; display: Display }) {
  const t = useTranslations("wellbeing");
  const locale = useLocale() as Locale;
  const id = useId();
  const [groupId, setGroupId] = useState(groups.find((g) => g.led)?.id ?? groups[0]?.id ?? "");
  const [days, setDays] = useState<number>(7);
  const [detail, setDetail] = useState<WellbeingGroupDetail | null>(null);
  const [failed, setFailed] = useState(false);
  const [reload, setReload] = useState(0);
  const [open, setOpen] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState("");

  useEffect(() => {
    if (!groupId) return;
    let live = true;
    getJson<WellbeingGroupDetail>(`/v1/wellbeing/groups/${groupId}?days=${days}`)
      .then((d) => { if (live) { setDetail(d); setFailed(false); } })
      .catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, [groupId, days, reload]);

  if (groups.length === 0) return <p>{t("noGroups")}</p>;
  return (
    <div className="space-y-6">
      <p className="max-w-3xl">{t("notForRanking")}</p>
      <div className="flex flex-wrap items-end gap-4">
        <div className="space-y-1">
          <label htmlFor={`${id}-group`} className="block font-semibold">{t("group")}</label>
          <select id={`${id}-group`} className={inputClass} value={groupId} onChange={(e) => { setGroupId(e.target.value); setOpen(null); }}>
            {groups.map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}{g.led ? ` (${t("youLead")})` : ""}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-days`} className="block font-semibold">{t("period")}</label>
          <select id={`${id}-days`} className={inputClass} value={days} onChange={(e) => setDays(Number(e.target.value))}>
            {wellbeingPeriods.map((d) => <option key={d} value={d}>{t("lastDays", { days: d })}</option>)}
          </select>
        </div>
      </div>
      {failed && <p role="alert">{t("unavailable")}</p>}
      {detail && (
        <section aria-labelledby={`${id}-group-heading`} className="space-y-4" data-testid="wellbeing-group">
          <h2 id={`${id}-group-heading`} className="text-2xl font-bold">{textOf(detail.name, locale)}</h2>
          <Totals totals={detail.totals} display={display} />
          {!detail.led ? (
            <p className="text-ink-muted">{t("totalsOnly")}</p>
          ) : (
            <>
              <ul className="space-y-3" data-testid="wellbeing-agents" aria-label={t("agentsLabel")}>
                {(detail.agents ?? []).map((agent) => (
                  <li key={agent.id} className={`space-y-2 rounded-lg border p-3 ${agent.alerts.length > 0 ? "border-2 border-warn" : "border-line"}`}>
                    <h3 className="text-lg font-semibold">{agent.name}{!agent.available && <span className="ms-2 text-sm font-normal text-ink-muted">{t("unavailableForAssignment")}</span>}</h3>
                    <Figures figures={agent.figures} days={detail.days} display={display} />
                    <Alerts alerts={agent.alerts} display={display} />
                    {agent.alerts.length > 0 && (
                      <button type="button" className={smallButton} aria-expanded={open === agent.id}
                        onClick={() => setOpen(open === agent.id ? null : agent.id)}>{t("suggestFor", { name: agent.name })}</button>
                    )}
                    {open === agent.id && <Suggestions groupId={detail.id} agent={agent} csrfToken={csrfToken} onMoved={() => setReload((r) => r + 1)} />}
                  </li>
                ))}
              </ul>
              <ThresholdsEditor key={detail.id} initial={detail.thresholds} busy={saving} legend={t("thresholds.groupLegend")}
                onSave={(values) => {
                  setSaving(true);
                  putJson(`/v1/wellbeing/groups/${detail.id}/thresholds`, values, csrfToken)
                    .then(() => { setSaved(t("thresholds.saved")); setReload((r) => r + 1); })
                    .catch(() => setSaved(t("thresholds.failed")))
                    .finally(() => setSaving(false));
                }} />
              <p aria-live="polite">{saved}</p>
            </>
          )}
        </section>
      )}
    </div>
  );
}

/** Account → My workload: the agent's own figures and the alerts about them (decided 2026-11-26). */
export function MyWorkload({ mine, display }: { mine: MyWellbeing; display: Display }) {
  const t = useTranslations("wellbeing.mine");
  const locale = useLocale() as Locale;
  if (!mine.enabled) return <p data-testid="my-workload-off">{t("off")}</p>;
  return (
    <div className="space-y-4" data-testid="my-workload">
      <p className="max-w-3xl">{t("intro")}</p>
      {mine.figures ? (
        <>
          <section aria-labelledby="mine-figures" className="space-y-2">
            <h2 id="mine-figures" className="text-xl font-bold">{t("figures", { days: mine.days })}</h2>
            <Figures figures={mine.figures.figures} days={mine.days} display={display} />
          </section>
          <section aria-labelledby="mine-alerts" className="space-y-2">
            <h2 id="mine-alerts" className="text-xl font-bold">{t("alerts")}</h2>
            <Alerts alerts={mine.figures.alerts} display={display} />
          </section>
        </>
      ) : <p>{t("noFigures")}</p>}
      {mine.groups.length > 0 && (
        <section aria-labelledby="mine-groups" className="space-y-2">
          <h2 id="mine-groups" className="text-xl font-bold">{t("groups")}</h2>
          <ul className="list-disc ps-5">
            {mine.groups.map((g) => (
              <li key={g.id}>{t("groupThresholds", { group: textOf(g.name, locale), load: g.thresholds.maxWeightedLoad, perDay: g.thresholds.maxNewPerDay, hours: g.thresholds.maxAfterHoursHours, percent: g.thresholds.maxDifficultPercent })}</li>
            ))}
          </ul>
        </section>
      )}
      <section aria-labelledby="mine-recorded" className="space-y-2">
        <h2 id="mine-recorded" className="text-xl font-bold">{t("recordedHeading")}</h2>
        <p className="max-w-3xl">{t("recorded")}</p>
      </section>
    </div>
  );
}

/** Settings → Wellbeing: on or off (with the confirmation that staff were told) and the default thresholds. */
export function WellbeingSettingsPanel({ initial, csrfToken }: { initial: WellbeingSettings; csrfToken: string }) {
  const t = useTranslations("wellbeing.settings");
  const id = useId();
  const [settings, setSettings] = useState(initial);
  const [informed, setInformed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState("");

  async function save(enabled: boolean, defaults: WellbeingThresholds) {
    setBusy(true);
    try {
      const next = await putJson<WellbeingSettings>("/v1/wellbeing/settings", { enabled, staffInformed: enabled ? informed : undefined, defaults }, csrfToken);
      setSettings(next);
      setStatus(t(enabled && !settings.enabled ? "turnedOn" : !enabled && settings.enabled ? "turnedOff" : "saved"));
    } catch {
      setStatus(t("failed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-6">
      <p className="max-w-3xl">{t("intro")}</p>
      <section aria-labelledby={`${id}-state`} className="space-y-3 rounded-lg border border-line p-4">
        <h3 id={`${id}-state`} className="text-lg font-semibold">{settings.enabled ? t("isOn") : t("isOff")}</h3>
        {settings.enabled ? (
          <>
            <p>{t("offHint")}</p>
            <button type="button" className={secondaryButton} disabled={busy} onClick={() => void save(false, settings.defaults)}>{t("turnOff")}</button>
          </>
        ) : (
          <>
            <p>{t("onHint")}</p>
            <label className="flex items-start gap-2">
              <input type="checkbox" className="mt-1" checked={informed} onChange={(e) => setInformed(e.target.checked)} />
              <span>{t("staffInformed")}</span>
            </label>
            <button type="button" className={primaryButton} disabled={busy || !informed} onClick={() => void save(true, settings.defaults)}>{t("turnOn")}</button>
          </>
        )}
      </section>
      <ThresholdsEditor key={JSON.stringify(settings.defaults)} initial={settings.defaults} busy={busy} legend={t("defaultsLegend")}
        onSave={(defaults) => void save(settings.enabled, defaults)} />
      <p aria-live="polite" data-testid="wellbeing-settings-status">{status}</p>
      <p className="text-sm text-ink-muted">{t("privacyHint")}</p>
    </div>
  );
}

"use client";

import { useId } from "react";
import { useTranslations } from "next-intl";
import { BilingualInputs, smallButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { ConditionsEditor, type WorkflowChoices } from "@/components/workflows/WorkflowEditors";
import type { ServiceFee } from "@/lib/catalog";
import { paymentTimings, type PaymentTiming } from "@/lib/payments";

/**
 * How a paid service is paid online (task E8): when (on submission, after approval, on
 * completion), through which gateway, and amounts added when the answers say so.
 */
export function FeePaymentEditor({ fee, gateways, choices, onChange }: {
  fee: ServiceFee;
  gateways: { id: string; name: string }[] | null;
  choices: WorkflowChoices;
  onChange: (fee: ServiceFee) => void;
}) {
  const t = useTranslations("payments.fee");
  const id = useId();
  const setRule = (i: number, patch: Partial<ServiceFee["rules"][number]>) => onChange({ ...fee, rules: fee.rules.map((r, j) => (j === i ? { ...r, ...patch } : r)) });
  return (
    <div className="space-y-3 rounded-md border border-line p-3" data-testid="fee-payment">
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-timing`} className="block text-sm font-semibold">{t("timing")}</label>
          <select id={`${id}-timing`} className={fieldInput} value={fee.timing} onChange={(e) => onChange({ ...fee, timing: e.target.value as PaymentTiming })}>
            {paymentTimings.map((v) => <option key={v} value={v}>{t(`timings.${v}`)}</option>)}
          </select>
        </div>
        {gateways && (
          <div className="space-y-1">
            <label htmlFor={`${id}-gateway`} className="block text-sm font-semibold">{t("gateway")}</label>
            <select id={`${id}-gateway`} className={fieldInput} value={fee.gatewayId ?? ""} onChange={(e) => onChange({ ...fee, gatewayId: e.target.value || null })}>
              <option value="">{t("defaultGateway")}</option>
              {gateways.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </div>
        )}
      </div>
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("rules")}</legend>
        <p className="text-sm text-ink-muted">{t("rulesHint")}</p>
        <ol className="space-y-2">
          {fee.rules.map((rule, i) => (
            <li key={i} className="space-y-2 rounded-md border border-line p-3" data-testid="fee-rule">
              <BilingualInputs id={`${id}-rule-${i}`} label={t("ruleName", { number: i + 1 })} en={rule.name.en ?? ""} ar={rule.name.ar ?? ""} maxLength={500}
                onChange={(name) => setRule(i, { name: { en: name.en || null, ar: name.ar || null } })} />
              <div className="max-w-xs space-y-1">
                <label htmlFor={`${id}-rule-${i}-amount`} className="block text-sm">{t("ruleAmount")}</label>
                <input id={`${id}-rule-${i}-amount`} type="number" min={0.01} step={0.01} dir="ltr" className={fieldInput} value={rule.amountAed || ""}
                  onChange={(e) => setRule(i, { amountAed: Number(e.target.value) })} />
              </div>
              <ConditionsEditor legend={t("when")} conditions={rule.conditions} choices={choices} onChange={(conditions) => setRule(i, { conditions })} />
              <button type="button" className={smallButton} onClick={() => onChange({ ...fee, rules: fee.rules.filter((_, j) => j !== i) })} aria-label={t("removeRule", { number: i + 1 })}>{t("remove")}</button>
            </li>
          ))}
        </ol>
        <button type="button" className={smallButton}
          onClick={() => onChange({ ...fee, rules: [...fee.rules, { name: { en: null, ar: null }, amountAed: 0, conditions: [{ field: "answer.", op: "eq", value: "" }] }] })}>
          {t("addRule")}
        </button>
      </fieldset>
    </div>
  );
}

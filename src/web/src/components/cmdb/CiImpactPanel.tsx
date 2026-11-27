"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldInput } from "@/components/forms/ui";
import { getJson } from "@/lib/client-api";
import { nodeLabel, textIn, type CiImpact, type CiImpactEntry } from "@/lib/cmdb";
import { formatNumber, type DigitSystem } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { NodeName } from "./CiRelationships";

const depths = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

/** One affected item or consumer, with how it is reached. */
function Entry({ entry }: { entry: CiImpactEntry }) {
  const t = useTranslations("cmdb.impact");
  const locale = useLocale() as Locale;
  return (
    <li>
      <NodeName node={entry.node} />{" "}
      <span className="text-sm text-ink-muted">
        {t("via", { depth: entry.depth, via: nodeLabel(entry.via, locale, "—"), relation: textIn(entry.relationship.outgoingName, locale) })}
      </span>
    </li>
  );
}

/**
 * What a fault of this item affects (task G1): business services, items and the users,
 * departments and customer groups that use them, following each relationship type's impact
 * direction up to the chosen depth.
 */
export function CiImpactPanel({ itemId, version, digits }: { itemId: string; version: number; digits: DigitSystem }) {
  const t = useTranslations("cmdb.impact");
  const locale = useLocale() as Locale;
  const id = useId();
  const [depth, setDepth] = useState(3);
  const [impact, setImpact] = useState<CiImpact | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let live = true;
    getJson<CiImpact>(`/v1/cmdb/items/${itemId}/impact?depth=${depth}`)
      .then((result) => live && (setImpact(result), setFailed(false)))
      .catch(() => live && setFailed(true));
    return () => {
      live = false;
    };
  }, [itemId, depth, version]);

  const services = impact?.businessServices ?? [];
  const others = impact?.items.filter((i) => !services.some((s) => s.node.id === i.node.id)) ?? [];
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="ci-impact">
      <h2 id={`${id}-heading`} className="text-xl font-bold">{t("heading")}</h2>
      <p className="text-sm">{t("intro")}</p>
      <div className="space-y-1">
        <label htmlFor={`${id}-depth`} className="block text-sm font-semibold">{t("depth")}</label>
        <select id={`${id}-depth`} value={depth} onChange={(e) => setDepth(Number(e.target.value))} className={`${fieldInput} max-w-40`}>
          {depths.map((d) => <option key={d} value={d}>{t("steps", { count: d })}</option>)}
        </select>
      </div>
      <div role="status" aria-live="polite" className="font-semibold">
        {failed ? t("failed") : impact ? t("summary", {
          services: formatNumber(services.length, locale, digits),
          items: formatNumber(impact.items.length, locale, digits),
          users: formatNumber(impact.userCount, locale, digits),
        }) : t("loading")}
      </div>
      {impact?.truncated && <p className="text-sm text-warn">{t("truncated")}</p>}
      {impact && (
        <div className="grid gap-4 md:grid-cols-3">
          <div>
            <h3 className="font-semibold">{t("services")}</h3>
            {services.length === 0 ? <p className="text-sm text-ink-muted">{t("noneFound")}</p> : <ul className="space-y-1">{services.map((e) => <Entry key={e.node.id} entry={e} />)}</ul>}
          </div>
          <div>
            <h3 className="font-semibold">{t("items")}</h3>
            {others.length === 0 ? <p className="text-sm text-ink-muted">{t("noneFound")}</p> : <ul className="space-y-1">{others.map((e) => <Entry key={e.node.id} entry={e} />)}</ul>}
          </div>
          <div>
            <h3 className="font-semibold">{t("consumers")}</h3>
            {impact.consumers.length === 0 ? <p className="text-sm text-ink-muted">{t("noneFound")}</p> : <ul className="space-y-1">{impact.consumers.map((e) => <Entry key={`${e.node.kind}-${e.node.id}`} entry={e} />)}</ul>}
          </div>
        </div>
      )}
    </section>
  );
}

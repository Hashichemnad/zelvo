"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { primaryButton, smallButton } from "@/components/directory/common";
import { fieldButton, fieldInput } from "@/components/forms/ui";
import { Link } from "@/i18n/navigation";
import { deleteRequest, getJson, postJson } from "@/lib/client-api";
import { nodeLabel, textIn, type CiNode, type CiPick, type CiRelationship, type CiRelationshipType } from "@/lib/cmdb";
import type { Department } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import type { CustomerGroup } from "@/lib/sla";
import { CiLookup } from "./CiLookup";
import { CiStatusBadge, CmdbStatusLine, useSaver } from "./common";

type ConsumerKind = "User" | "Department" | "CustomerGroup";

/** The other end of a relationship: a link to items, the name of people, departments and customer groups. */
export function NodeName({ node, linkItems = true }: { node: CiNode; linkItems?: boolean }) {
  const t = useTranslations("cmdb");
  const locale = useLocale() as Locale;
  const label = nodeLabel(node, locale, t("unknownNode"));
  if (node.kind === "ConfigurationItem") {
    return linkItems ? (
      <Link href={`/cmdb/${node.id}`} className="font-semibold text-brand underline-offset-4 hover:underline" dir="auto">{label}</Link>
    ) : (
      <span className="font-semibold" dir="auto">{label}</span>
    );
  }
  return (
    <span dir="auto">
      <span className="font-semibold">{label}</span> <span className="text-sm text-ink-muted">({t(`nodeKinds.${node.kind}`)})</span>
    </span>
  );
}

/** Picks the user, department or customer group a "used by" relationship points at. */
function ConsumerPicker({ id, kind, onPick }: { id: string; kind: ConsumerKind; onPick: (target: { id: string; name: string }) => void }) {
  const t = useTranslations("cmdb.relationships");
  const locale = useLocale() as Locale;
  const [query, setQuery] = useState("");
  const [people, setPeople] = useState<{ id: string; displayName: string | null }[] | null>(null);
  const [choices, setChoices] = useState<{ id: string; name: string }[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (kind === "User") return;
    let live = true;
    const load = kind === "Department"
      ? getJson<Department[]>("/v1/departments").then((list) => list.filter((d) => d.active).map((d) => ({ id: d.id, name: textIn(d.name, locale) })))
      : getJson<CustomerGroup[]>("/v1/sla/customer-groups").then((list) => list.filter((g) => g.active).map((g) => ({ id: g.id, name: textIn(g.name, locale) })));
    load.then((list) => live && setChoices(list)).catch(() => live && setFailed(true));
    return () => {
      live = false;
    };
  }, [kind, locale]);

  if (kind !== "User") {
    return (
      <div className="space-y-1">
        <label htmlFor={id} className="block text-sm font-semibold">{t(kind === "Department" ? "department" : "customerGroup")}</label>
        {failed && <p role="alert" className="text-sm">{t("choicesFailed")}</p>}
        <select id={id} defaultValue="" className={`${fieldInput} max-w-md`}
          onChange={(e) => {
            const chosen = choices?.find((c) => c.id === e.target.value);
            if (chosen) onPick(chosen);
          }}>
          <option value="">{t("choose")}</option>
          {(choices ?? []).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <label htmlFor={id} className="block text-sm font-semibold">{t("person")}</label>
      <div className="flex flex-wrap gap-2">
        <input id={id} type="search" dir="auto" value={query} maxLength={200} onChange={(e) => setQuery(e.target.value)} className={`${fieldInput} max-w-xs`}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              if (query.trim().length >= 2) void search();
            }
          }} />
        <button type="button" className={fieldButton} disabled={query.trim().length < 2} onClick={() => void search()}>{t("searchPeople")}</button>
      </div>
      <p role="status" aria-live="polite" className="text-sm">{failed ? t("choicesFailed") : people ? t("peopleFound", { count: people.length }) : ""}</p>
      {people && people.length > 0 && (
        <ul className="space-y-1">
          {people.map((p) => (
            <li key={p.id}>
              <button type="button" className={fieldButton} onClick={() => {
                onPick({ id: p.id, name: p.displayName ?? p.id });
                setPeople(null);
                setQuery("");
              }}>
                {t("pickPerson", { name: p.displayName ?? p.id })}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );

  async function search() {
    setFailed(false);
    try {
      setPeople(await getJson(`/v1/forms/people?q=${encodeURIComponent(query.trim())}`));
    } catch {
      setFailed(true);
    }
  }
}

/**
 * The item's relationships (task G1), from it and to it, grouped by the type's name in that
 * direction ("runs on", "hosts"), with adding and removing for holders of cmdb.manage.
 */
export function CiRelationships({
  itemId,
  types,
  canManage,
  csrfToken,
  version,
  onChanged,
}: {
  itemId: string;
  types: CiRelationshipType[];
  canManage: boolean;
  csrfToken: string;
  /** Changes when relationships changed elsewhere on the page. */
  version: number;
  onChanged: () => void;
}) {
  const t = useTranslations("cmdb.relationships");
  const locale = useLocale() as Locale;
  const id = useId();
  const [relationships, setRelationships] = useState<CiRelationship[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [typeId, setTypeId] = useState("");
  const [consumerKind, setConsumerKind] = useState<ConsumerKind>("User");
  const [target, setTarget] = useState<{ id: string; name: string } | null>(null);
  const [note, setNote] = useState("");
  const { status, run, busy } = useSaver();
  const type = types.find((x) => x.id === typeId);

  useEffect(() => {
    let live = true;
    getJson<CiRelationship[]>(`/v1/cmdb/items/${itemId}/relationships`)
      .then((list) => live && (setRelationships(list), setFailed(false)))
      .catch(() => live && setFailed(true));
    return () => {
      live = false;
    };
  }, [itemId, version]);

  const groups = new Map<string, { label: string; items: CiRelationship[] }>();
  for (const r of relationships ?? []) {
    const key = `${r.type.id}:${r.direction}`;
    const group = groups.get(key) ?? { label: textIn(r.name, locale) || r.type.key, items: [] };
    group.items.push(r);
    groups.set(key, group);
  }

  function add() {
    if (!type || !target) return;
    const body: Record<string, string | null> = { typeId: type.id, note: note.trim() || null };
    if (type.targetKind === "ConfigurationItem") body.targetId = target.id;
    else body[consumerKind === "User" ? "targetUserId" : consumerKind === "Department" ? "targetDepartmentId" : "targetCustomerGroupId"] = target.id;
    void run(async () => {
      await postJson(`/v1/cmdb/items/${itemId}/relationships`, body, csrfToken);
      setTarget(null);
      setNote("");
      onChanged();
    });
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3">
      <h2 id={`${id}-heading`} className="text-xl font-bold">{t("heading")}</h2>
      {failed && <p role="alert">{t("failed")}</p>}
      {relationships && relationships.length === 0 && <p>{t("none")}</p>}
      {[...groups.values()].map((group) => (
        <div key={group.label + group.items[0]!.direction} className="space-y-1">
          <h3 className="font-semibold">{group.label}</h3>
          <ul className="space-y-1 ps-4" data-testid="ci-relationship-group">
            {group.items.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-2">
                <NodeName node={r.other} />
                {r.other.status && r.other.status !== "Active" && <CiStatusBadge status={r.other.status} />}
                {r.note && <span className="text-sm text-ink-muted" dir="auto">— {r.note}</span>}
                {canManage && (
                  <button type="button" className={smallButton} disabled={busy}
                    onClick={() => void run(async () => {
                      await deleteRequest(`/v1/cmdb/relationships/${r.id}`, csrfToken);
                      onChanged();
                    })}>
                    {t("remove", { relation: group.label, name: nodeLabel(r.other, locale, "") })}
                  </button>
                )}
              </li>
            ))}
          </ul>
        </div>
      ))}

      {canManage && (
        <fieldset className="space-y-3 rounded-lg border border-line p-3">
          <legend className="px-1 font-semibold">{t("add")}</legend>
          <div className="space-y-1">
            <label htmlFor={`${id}-type`} className="block text-sm font-semibold">{t("type")}</label>
            <select id={`${id}-type`} value={typeId} className={`${fieldInput} max-w-md`}
              onChange={(e) => {
                setTypeId(e.target.value);
                setTarget(null);
              }}>
              <option value="">{t("choose")}</option>
              {types.filter((x) => x.active).map((x) => (
                <option key={x.id} value={x.id}>{t("typeOption", { name: textIn(x.outgoingName, locale) || x.key })}</option>
              ))}
            </select>
          </div>
          {type?.targetKind === "Consumer" && (
            <fieldset className="space-y-1">
              <legend className="text-sm font-semibold">{t("consumerKind")}</legend>
              <div className="flex flex-wrap gap-4">
                {(["User", "Department", "CustomerGroup"] as const).map((k) => (
                  <label key={k} className="flex items-center gap-2">
                    <input type="radio" name={`${id}-kind`} checked={consumerKind === k} className="size-5 accent-brand"
                      onChange={() => {
                        setConsumerKind(k);
                        setTarget(null);
                      }} />
                    {t(`kinds.${k}`)}
                  </label>
                ))}
              </div>
            </fieldset>
          )}
          {type && !target && (type.targetKind === "ConfigurationItem"
            ? <CiLookup id={`${id}-target`} label={t("findItem")} exclude={[itemId]} onPick={(item: CiPick) => setTarget({ id: item.id, name: `${item.number} ${item.name}` })} />
            : <ConsumerPicker key={consumerKind} id={`${id}-target`} kind={consumerKind} onPick={setTarget} />)}
          {type && target && (
            <p className="flex flex-wrap items-center gap-2">
              <span>{t("chosen", { relation: textIn(type.outgoingName, locale), name: target.name })}</span>
              <button type="button" className={smallButton} onClick={() => setTarget(null)}>{t("chooseOther")}</button>
            </p>
          )}
          <div className="space-y-1">
            <label htmlFor={`${id}-note`} className="block text-sm font-semibold">{t("note")}</label>
            <input id={`${id}-note`} dir="auto" value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} className={`${fieldInput} max-w-md`} />
          </div>
          <button type="button" className={primaryButton} disabled={busy || !type || !target} onClick={add}>{t("save")}</button>
        </fieldset>
      )}
      <CmdbStatusLine status={status} />
    </section>
  );
}

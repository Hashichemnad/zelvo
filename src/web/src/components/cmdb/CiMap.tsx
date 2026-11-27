"use client";

import { useCallback, useEffect, useId, useState, useSyncExternalStore } from "react";
import { useLocale, useTranslations } from "next-intl";
import dynamic from "next/dynamic";
import { fieldButton, fieldInput } from "@/components/forms/ui";
import { useRouter } from "@/i18n/navigation";
import { getJson } from "@/lib/client-api";
import {
  ciFamilies,
  defaultMapFilters,
  mapQuery,
  neighbours,
  nodeFamily,
  nodeLabel,
  textIn,
  type CiClass,
  type CiFamily,
  type CiMap as CiMapData,
  type CiRelationshipType,
  type MapFilters,
} from "@/lib/cmdb";
import type { Locale } from "@/lib/incidents";
import type { MapCanvasHandle } from "./CiMapCanvas";
import { familyColourVariables } from "./familyColours";
import { CiMapList } from "./CiMapList";
import { familySymbols } from "./common";

// Cytoscape draws on a canvas in the browser only; the page renders on the server without it.
const CiMapCanvas = dynamic(() => import("./CiMapCanvas").then((m) => m.CiMapCanvas), { ssr: false });

type View = "map" | "list";
const viewKey = "zelvo.cmdb.mapView";

const noSubscription = () => () => {};

function rememberedView(): View | null {
  try {
    const value = window.localStorage.getItem(viewKey);
    return value === "map" || value === "list" ? value : null;
  } catch {
    return null;
  }
}

/**
 * The relationship map of an item (task G1, decision 5): the picture (zoom, pan, depth, filters,
 * keyboard) or the equal list view, with the same filters; the selected node is always announced
 * in the page's language. The choice of view is remembered on the device.
 */
export function CiMap({
  itemId,
  types,
  classes,
  version,
  initialView,
}: {
  itemId: string;
  types: CiRelationshipType[];
  classes: CiClass[];
  version: number;
  /** For tests and pages that want the list first; otherwise the device's last choice, else the map. */
  initialView?: View;
}) {
  const t = useTranslations("cmdb.map");
  const tFamilies = useTranslations("cmdb.families");
  const tCmdb = useTranslations("cmdb");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [chosenView, setView] = useState<View | null>(initialView ?? null);
  // The device's last choice, read after hydration (the server has no storage).
  const remembered = useSyncExternalStore(noSubscription, rememberedView, () => null);
  const view: View = chosenView ?? remembered ?? "map";
  const [filters, setFilters] = useState<MapFilters>(defaultMapFilters);
  const [map, setMap] = useState<CiMapData | null>(null);
  const [failed, setFailed] = useState(false);
  const [selectedId, setSelectedId] = useState(itemId);
  const [handle, setHandle] = useState<MapCanvasHandle | null>(null);
  const controls = useCallback((h: MapCanvasHandle | null) => setHandle(h), []);

  useEffect(() => {
    let live = true;
    getJson<CiMapData>(`/v1/cmdb/items/${itemId}/map?${mapQuery(filters)}`)
      .then((result) => {
        if (!live) return;
        setMap(result);
        setFailed(false);
        setSelectedId((current) => (result.nodes.some((n) => n.node.id === current) ? current : itemId));
      })
      .catch(() => live && setFailed(true));
    return () => {
      live = false;
    };
  }, [itemId, filters, version]);

  function choose(next: View) {
    setView(next);
    try {
      window.localStorage.setItem(viewKey, next);
    } catch {
      // Remembering the view is a convenience only.
    }
  }

  const toggle = (list: string[], value: string, on: boolean) => (on ? [...list, value] : list.filter((x) => x !== value));
  const unknown = tCmdb("unknownNode");
  const selected = map?.nodes.find((n) => n.node.id === selectedId)?.node;
  const announcement = (() => {
    if (!map || !selected) return "";
    const links = neighbours(map, selected.id).map(({ node, edge, outgoing }) =>
      t(outgoing ? "linkOut" : "linkIn", {
        relation: textIn(outgoing ? edge.type.outgoingName : edge.type.incomingName, locale) || edge.type.key,
        name: nodeLabel(node, locale, unknown),
      }));
    return t("selected", {
      name: nodeLabel(selected, locale, unknown),
      family: tFamilies(nodeFamily(selected)),
      count: links.length,
      links: links.join("; "),
    });
  })();
  const usedFamilies = new Set<CiFamily | "People">(map?.nodes.map((n) => nodeFamily(n.node)) ?? []);

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3" data-testid="ci-map">
      <h2 id={`${id}-heading`} className="text-xl font-bold">{t("heading")}</h2>
      <div className="flex flex-wrap gap-2" role="group" aria-label={t("viewChoice")}>
        <button type="button" className={fieldButton} aria-pressed={view === "map"} onClick={() => choose("map")}>{t("viewMap")}</button>
        <button type="button" className={fieldButton} aria-pressed={view === "list"} onClick={() => choose("list")}>{t("viewList")}</button>
      </div>

      <fieldset className="flex flex-wrap items-start gap-4 rounded-lg border border-line p-3">
        <legend className="px-1 font-semibold">{t("filters")}</legend>
        <div className="space-y-1">
          <label htmlFor={`${id}-depth`} className="block text-sm font-semibold">{t("depth")}</label>
          <select id={`${id}-depth`} value={filters.depth} onChange={(e) => setFilters({ ...filters, depth: Number(e.target.value) })} className={`${fieldInput} max-w-40`}>
            {[1, 2, 3, 4].map((d) => <option key={d} value={d}>{t("steps", { count: d })}</option>)}
          </select>
        </div>
        <details className="min-w-48">
          <summary className="cursor-pointer text-sm font-semibold">
            {t("types", { count: filters.typeIds.length })}
          </summary>
          <ul className="mt-2 space-y-1">
            {types.map((type) => (
              <li key={type.id}>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" className="size-5 accent-brand" checked={filters.typeIds.includes(type.id)}
                    onChange={(e) => setFilters({ ...filters, typeIds: toggle(filters.typeIds, type.id, e.target.checked) })} />
                  {textIn(type.outgoingName, locale) || type.key}
                </label>
              </li>
            ))}
          </ul>
        </details>
        <details className="min-w-48">
          <summary className="cursor-pointer text-sm font-semibold">
            {t("classes", { count: filters.classIds.length })}
          </summary>
          <ul className="mt-2 max-h-64 space-y-1 overflow-y-auto">
            {classes.filter((c) => c.active).map((c) => (
              <li key={c.id} style={{ paddingInlineStart: `${(c.depth - 1) * 1}rem` }}>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" className="size-5 accent-brand" checked={filters.classIds.includes(c.id)}
                    onChange={(e) => setFilters({ ...filters, classIds: toggle(filters.classIds, c.id, e.target.checked) })} />
                  {textIn(c.name, locale) || c.key}
                </label>
              </li>
            ))}
          </ul>
        </details>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" className="size-5 accent-brand" checked={filters.consumers} onChange={(e) => setFilters({ ...filters, consumers: e.target.checked })} />
          {t("showPeople")}
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" className="size-5 accent-brand" checked={filters.retired} onChange={(e) => setFilters({ ...filters, retired: e.target.checked })} />
          {t("showRetired")}
        </label>
      </fieldset>

      {failed && <p role="alert">{t("failed")}</p>}
      {map?.truncated && <p className="text-sm text-warn">{t("truncated")}</p>}

      {map && view === "map" && (
        <div className="space-y-2">
          <div className="flex flex-wrap gap-2" role="toolbar" aria-label={t("zoomTools")}>
            <button type="button" className={fieldButton} disabled={!handle} onClick={() => handle?.zoomIn()}>{t("zoomIn")}</button>
            <button type="button" className={fieldButton} disabled={!handle} onClick={() => handle?.zoomOut()}>{t("zoomOut")}</button>
            <button type="button" className={fieldButton} disabled={!handle} onClick={() => handle?.fit()}>{t("fit")}</button>
            {selected?.kind === "ConfigurationItem" && selected.id !== itemId && (
              <button type="button" className={fieldButton} onClick={() => router.push(`/cmdb/${selected.id}`)}>
                {t("open", { name: nodeLabel(selected, locale, unknown) })}
              </button>
            )}
          </div>
          <p id={`${id}-keys`} className="text-sm text-ink-muted">{t("keys")}</p>
          <CiMapCanvas map={map} centreId={itemId} selectedId={selectedId} onSelect={setSelectedId} controls={controls} describedBy={`${id}-keys`}
            onOpen={(target) => {
              const node = map.nodes.find((n) => n.node.id === target)?.node;
              if (node?.kind === "ConfigurationItem" && node.id !== itemId) router.push(`/cmdb/${node.id}`);
            }} />
          <div aria-label={t("legend")} role="group" className="flex flex-wrap gap-3 text-sm">
            {[...ciFamilies, "People" as const].filter((f) => usedFamilies.has(f)).map((f) => (
              <span key={f} className="inline-flex items-center gap-1">
                <span aria-hidden="true" className="inline-block rounded-sm border-2 px-1 leading-none" style={{ borderColor: `var(${familyColourVariables[f]})` }}>
                  {familySymbols[f]}
                </span>
                {tFamilies(f)}
              </span>
            ))}
            <span className="inline-flex items-center gap-1">
              <span aria-hidden="true" className="inline-block rounded-sm border-2 border-dashed border-ink-muted px-1 leading-none">◌</span>
              {t("legendRetired")}
            </span>
          </div>
        </div>
      )}
      {map && view === "list" && <CiMapList map={map} centreId={itemId} />}

      {/* Always present, so the selection is announced in the page's language. */}
      <p role="status" aria-live="polite" className={view === "map" ? "text-sm" : "sr-only"} data-testid="ci-map-announcement">{view === "map" ? announcement : ""}</p>
    </section>
  );
}

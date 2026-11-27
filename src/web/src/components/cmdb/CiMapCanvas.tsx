"use client";

import { useEffect, useRef, useState } from "react";
import type cytoscape from "cytoscape";
import { useLocale, useTranslations } from "next-intl";
import { nodeFamily, nodeLabel, textIn, type CiFamily, type CiMap } from "@/lib/cmdb";
import type { Locale } from "@/lib/incidents";
import { familyShapes } from "./common";
import { familyColourVariables } from "./familyColours";

/** The colour tokens as they apply now (dark mode and the tenant's branding included). */
function tokens() {
  const style = getComputedStyle(document.documentElement);
  const read = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  return {
    ink: read("--color-ink", "#1a1f24"),
    muted: read("--color-ink-muted", "#4a5560"),
    surface: read("--color-surface", "#ffffff"),
    line: read("--color-line", "#d5dce3"),
    brand: read("--color-brand", "#0f5c8a"),
    font: getComputedStyle(document.body).fontFamily || "sans-serif",
    family: Object.fromEntries(Object.entries(familyColourVariables).map(([f, v]) => [f, read(v, "#4a5560")])) as Record<CiFamily | "People", string>,
  };
}

function stylesheet(): cytoscape.StylesheetJson {
  const c = tokens();
  return [
    {
      selector: "node",
      style: {
        shape: "data(shape)" as never,
        "background-color": c.surface,
        "border-width": 3,
        "border-color": "data(colour)",
        label: "data(label)",
        color: c.ink,
        "font-family": c.font,
        "font-size": 12,
        "text-valign": "bottom",
        "text-margin-y": 6,
        "text-wrap": "wrap",
        "text-max-width": "140px",
        "text-background-color": c.surface,
        "text-background-opacity": 0.85,
        "text-background-padding": "2px",
        width: 34,
        height: 34,
      },
    },
    { selector: "node.centre", style: { "border-width": 5, width: 44, height: 44, "font-weight": "bold" } },
    { selector: "node.retired", style: { "border-style": "dashed" } },
    { selector: "node:selected", style: { "overlay-color": c.brand, "overlay-opacity": 0.25, "overlay-padding": 8, "underlay-color": c.brand, "underlay-opacity": 0.35, "underlay-padding": 6 } },
    {
      selector: "edge",
      style: {
        width: 2,
        "line-color": c.muted,
        "target-arrow-color": c.muted,
        "target-arrow-shape": "triangle",
        "curve-style": "bezier",
        label: "data(label)",
        "font-family": c.font,
        "font-size": 10,
        color: c.ink,
        "text-rotation": "autorotate",
        "text-background-color": c.surface,
        "text-background-opacity": 0.9,
        "text-background-padding": "2px",
      },
    },
    { selector: "edge.highlighted", style: { width: 3, "line-color": c.brand, "target-arrow-color": c.brand } },
  ];
}

/** Direction of an arrow key on the screen (the canvas is never mirrored, so arrows are as seen). */
const arrowVectors: Record<string, [number, number]> = { ArrowRight: [1, 0], ArrowLeft: [-1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };

export interface MapCanvasHandle {
  zoomIn: () => void;
  zoomOut: () => void;
  fit: () => void;
}

/**
 * The picture of the relationship map (task G1, decision 5), drawn with Cytoscape.js on a canvas
 * and loaded only in the browser. Wheel and buttons zoom, dragging pans; the focusable frame takes
 * the keyboard: arrow keys move to the connected node in that direction, Home goes back to the
 * item, Enter opens the selected item, + and − zoom and 0 fits the map.
 */
export function CiMapCanvas({
  map,
  centreId,
  selectedId,
  onSelect,
  onOpen,
  controls,
  describedBy,
}: {
  map: CiMap;
  centreId: string;
  selectedId: string;
  onSelect: (id: string) => void;
  onOpen: (id: string) => void;
  /** Receives the zoom and fit actions for the buttons beside the map. */
  controls: (handle: MapCanvasHandle | null) => void;
  describedBy: string;
}) {
  const t = useTranslations("cmdb.map");
  const tCmdb = useTranslations("cmdb");
  const locale = useLocale() as Locale;
  const unknown = tCmdb("unknownNode");
  const container = useRef<HTMLDivElement>(null);
  const cy = useRef<cytoscape.Core | null>(null);
  const [failed, setFailed] = useState(false);
  const latest = useRef({ onSelect, onOpen, selectedId });
  useEffect(() => {
    latest.current = { onSelect, onOpen, selectedId };
  });

  // Build the graph when the map changes.
  useEffect(() => {
    let live = true;
    import("cytoscape")
      .then(({ default: cytoscapeFactory }) => {
        if (!live || !container.current) return;
        const c = tokens();
        const elements: cytoscape.ElementDefinition[] = [
          ...map.nodes.map(({ node, depth }) => {
            const family = nodeFamily(node);
            const label = node.kind === "ConfigurationItem" ? `${node.number ?? ""}\n${node.name ?? ""}`.trim() : nodeLabel(node, locale, unknown);
            return {
              group: "nodes" as const,
              data: { id: node.id, label, depth, shape: familyShapes[family], colour: c.family[family], family },
              classes: [node.id === centreId ? "centre" : "", node.status === "Retired" ? "retired" : ""].filter(Boolean).join(" "),
            };
          }),
          ...map.edges.map((edge) => ({
            group: "edges" as const,
            data: { id: edge.id, source: edge.source, target: edge.target, label: textIn(edge.type.outgoingName, locale) || edge.type.key },
          })),
        ];
        cy.current?.destroy();
        const maxDepth = Math.max(1, ...map.nodes.map((n) => n.depth));
        const instance = cytoscapeFactory({
          container: container.current,
          elements,
          style: stylesheet(),
          layout: { name: "concentric", concentric: (n: cytoscape.NodeSingular) => maxDepth + 1 - Number(n.data("depth")), levelWidth: () => 1, minNodeSpacing: 40, animate: false },
          minZoom: 0.2,
          maxZoom: 3,
          boxSelectionEnabled: false,
          selectionType: "single",
        });
        instance.on("tap", "node", (event) => latest.current.onSelect(event.target.id()));
        instance.on("dbltap", "node", (event) => latest.current.onOpen(event.target.id()));
        instance.$id(latest.current.selectedId).select();
        cy.current = instance;
        controls({
          zoomIn: () => instance.zoom({ level: instance.zoom() * 1.25, renderedPosition: { x: instance.width() / 2, y: instance.height() / 2 } }),
          zoomOut: () => instance.zoom({ level: instance.zoom() / 1.25, renderedPosition: { x: instance.width() / 2, y: instance.height() / 2 } }),
          fit: () => instance.fit(undefined, 30),
        });
      })
      .catch(() => live && setFailed(true));
    return () => {
      live = false;
    };
  }, [map, centreId, locale, controls, unknown]);

  useEffect(() => () => {
    cy.current?.destroy();
    cy.current = null;
    controls(null);
  }, [controls]);

  // Colours follow dark mode while the page is open.
  useEffect(() => {
    const query = window.matchMedia?.("(prefers-color-scheme: dark)");
    if (!query) return;
    const restyle = () => {
      const instance = cy.current;
      if (!instance) return;
      const c = tokens();
      instance.nodes().forEach((n) => {
        n.data("colour", c.family[n.data("family") as CiFamily | "People"]);
      });
      instance.style(stylesheet());
    };
    query.addEventListener("change", restyle);
    return () => query.removeEventListener("change", restyle);
  }, []);

  // Show the selection made with the keyboard or the list.
  useEffect(() => {
    const instance = cy.current;
    if (!instance) return;
    instance.elements().unselect().removeClass("highlighted");
    const node = instance.$id(selectedId);
    if (node.nonempty()) {
      node.select();
      node.connectedEdges().addClass("highlighted");
      const box = instance.extent();
      const p = node.position();
      if (p.x < box.x1 || p.x > box.x2 || p.y < box.y1 || p.y > box.y2) instance.center(node);
    }
  }, [selectedId]);

  function onKeyDown(event: React.KeyboardEvent) {
    const instance = cy.current;
    if (!instance) return;
    const handled = () => {
      event.preventDefault();
      event.stopPropagation();
    };
    if (event.key in arrowVectors) {
      handled();
      const current = instance.$id(selectedId);
      if (current.empty()) return onSelect(centreId);
      const [dx, dy] = arrowVectors[event.key]!;
      const from = current.position();
      let best: { id: string; score: number } | null = null;
      current.neighborhood("node").forEach((n) => {
        const p = n.position();
        const vx = p.x - from.x;
        const vy = p.y - from.y;
        const length = Math.hypot(vx, vy) || 1;
        const cos = (vx * dx + vy * dy) / length;
        if (cos <= 0.2) return;
        // Prefer nodes straight in that direction, then nearer ones.
        const score = cos * 1000 - length;
        if (!best || score > best.score) best = { id: n.id(), score };
      });
      if (best) onSelect((best as { id: string }).id);
    } else if (event.key === "Home") {
      handled();
      onSelect(centreId);
    } else if (event.key === "Enter") {
      handled();
      onOpen(selectedId);
    } else if (event.key === "+" || event.key === "=") {
      handled();
      instance.zoom({ level: instance.zoom() * 1.25, renderedPosition: { x: instance.width() / 2, y: instance.height() / 2 } });
    } else if (event.key === "-" || event.key === "_") {
      handled();
      instance.zoom({ level: instance.zoom() / 1.25, renderedPosition: { x: instance.width() / 2, y: instance.height() / 2 } });
    } else if (event.key === "0") {
      handled();
      instance.fit(undefined, 30);
    }
  }

  if (failed) return <p role="alert">{t("drawFailed")}</p>;
  return (
    <div ref={container} tabIndex={0} role="application" aria-roledescription={t("roleDescription")} aria-label={t("canvasLabel")}
      aria-describedby={describedBy} onKeyDown={onKeyDown} data-testid="ci-map-canvas"
      className="h-[28rem] w-full rounded-lg border border-line bg-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand" />
  );
}

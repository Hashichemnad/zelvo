"use client";

import { useLocale, useTranslations } from "next-intl";
import { cellClass, tableClass } from "@/components/directory/common";
import { mapLines, nodeFamily, nodeLabel, textIn, type CiMap } from "@/lib/cmdb";
import type { Locale } from "@/lib/incidents";
import { NodeName } from "./CiRelationships";
import { CiStatusBadge, FamilyLabel } from "./common";

/**
 * The relationship map as a list (task G1, decision 5): the same nodes and relationships as the
 * picture, with the same filters, for keyboard and screen-reader users. Each relationship is read
 * from its source: "srv-web-01 — runs on → vm-host-03".
 */
export function CiMapList({ map, centreId }: { map: CiMap; centreId: string }) {
  const t = useTranslations("cmdb.map");
  const tCmdb = useTranslations("cmdb");
  const locale = useLocale() as Locale;
  const lines = mapLines(map);
  const unknown = tCmdb("unknownNode");
  return (
    <div className="space-y-4" data-testid="ci-map-list">
      <div className="overflow-x-auto">
        <table className={tableClass}>
          <caption className="text-start font-semibold">{t("nodesCaption", { count: map.nodes.length })}</caption>
          <thead>
            <tr>
              <th scope="col" className={cellClass}>{t("node")}</th>
              <th scope="col" className={cellClass}>{t("family")}</th>
              <th scope="col" className={cellClass}>{t("status")}</th>
              <th scope="col" className={cellClass}>{t("distance")}</th>
            </tr>
          </thead>
          <tbody>
            {map.nodes.map(({ node, depth }) => (
              <tr key={`${node.kind}-${node.id}`}>
                <th scope="row" className={`${cellClass} font-normal`}>
                  <NodeName node={node} linkItems={node.id !== centreId} />
                  {node.id === centreId && <span className="ms-2 text-sm text-ink-muted">({t("thisItem")})</span>}
                </th>
                <td className={cellClass}>
                  <FamilyLabel family={nodeFamily(node)} />
                  {node.class && <span className="block text-xs text-ink-muted">{textIn(node.class.name, locale)}</span>}
                </td>
                <td className={cellClass}>{node.status ? <CiStatusBadge status={node.status} /> : "—"}</td>
                <td className={cellClass}>{t("steps", { count: depth })}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="space-y-1">
        <h3 className="font-semibold">{t("edgesCaption", { count: lines.length })}</h3>
        {lines.length === 0 ? (
          <p className="text-sm">{t("noEdges")}</p>
        ) : (
          <ul className="space-y-1" data-testid="ci-map-lines">
            {lines.map(({ edge, source, target }) => (
              <li key={edge.id} dir="auto">
                {t("line", { source: nodeLabel(source, locale, unknown), relation: textIn(edge.type.outgoingName, locale) || edge.type.key, target: nodeLabel(target, locale, unknown) })}
                {edge.note && <span className="text-sm text-ink-muted"> — {edge.note}</span>}
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

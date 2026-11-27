"use client";

import { useState } from "react";
import type { CiClass, CiRelationshipType } from "@/lib/cmdb";
import type { DigitSystem } from "@/lib/format";
import { CiImpactPanel } from "./CiImpactPanel";
import { CiMap } from "./CiMap";
import { CiRelationships } from "./CiRelationships";

/** Relationships, impact and the map of one item, reloaded together when a relationship changes. */
export function CiRelationsArea({ itemId, types, classes, canManage, csrfToken, digits }: {
  itemId: string;
  types: CiRelationshipType[];
  classes: CiClass[];
  canManage: boolean;
  csrfToken: string;
  digits: DigitSystem;
}) {
  const [version, setVersion] = useState(0);
  return (
    <>
      <CiRelationships itemId={itemId} types={types} canManage={canManage} csrfToken={csrfToken} version={version} onChanged={() => setVersion((v) => v + 1)} />
      <CiImpactPanel itemId={itemId} version={version} digits={digits} />
      <CiMap itemId={itemId} types={types} classes={classes} version={version} />
    </>
  );
}

"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { deleteRequest, getJson, patchJson, postJson } from "@/lib/client-api";
import { maxTreeDepth, toInput, type TreeKind, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { BilingualInputs, Localized, SaveStatusLine, primaryButton, smallButton, useSaver } from "./common";

type Editing = { mode: "add"; parentId: string | null } | { mode: "rename"; node: TreeNode } | null;

/**
 * Settings → Categories and Settings → Locations: a tree of up to three levels (decision
 * 2026-11-23). Administrators add, rename, reorder, activate and delete nodes; nodes in use
 * cannot be deleted and are made inactive instead.
 */
export function TreeEditor({ kind, initial, csrfToken, canManage }: { kind: TreeKind; initial: TreeNode[]; csrfToken: string; canManage: boolean }) {
  const t = useTranslations(`directory.${kind}`);
  const tTree = useTranslations("directory.tree");
  const locale = useLocale() as Locale;
  const id = useId();
  const [nodes, setNodes] = useState(initial);
  const [editing, setEditing] = useState<Editing>(null);
  const [name, setName] = useState({ en: "", ar: "" });
  const { status, run, busy } = useSaver();
  const base = `/v1/${kind}`;

  const reload = async () => setNodes(await getJson<TreeNode[]>(`${base}?includeInactive=true`));
  const change = (action: () => Promise<unknown>) =>
    run(async () => {
      await action();
      await reload();
      setEditing(null);
    });

  const childrenOf = (parentId: string | null) =>
    nodes.filter((n) => n.parentId === parentId).sort((a, b) => a.position - b.position);

  function startEditing(next: Editing) {
    setEditing(next);
    setName(next?.mode === "rename" ? { en: next.node.name.en ?? "", ar: next.node.name.ar ?? "" } : { en: "", ar: "" });
  }

  const form = (heading: string) => (
    <form
      className="space-y-2 rounded-lg border border-line p-3"
      aria-label={heading}
      onSubmit={(e) => {
        e.preventDefault();
        if (!editing) return;
        void change(() =>
          editing.mode === "add"
            ? postJson(base, { name: toInput(name.en, name.ar), parentId: editing.parentId }, csrfToken)
            : patchJson(`${base}/${editing.node.id}`, { name: toInput(name.en, name.ar) }, csrfToken),
        );
      }}
    >
      <BilingualInputs id={`${id}-name`} label={heading} en={name.en} ar={name.ar} onChange={setName} />
      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryButton} disabled={busy || (!name.en.trim() && !name.ar.trim())}>
          {tTree("save")}
        </button>
        <button type="button" className={smallButton} onClick={() => setEditing(null)}>
          {tTree("cancel")}
        </button>
      </div>
    </form>
  );

  const renderLevel = (parentId: string | null) => {
    const level = childrenOf(parentId);
    if (level.length === 0) return null;
    return (
      <ul className={parentId ? "mt-2 space-y-2 border-s border-line ps-4" : "space-y-2"}>
        {level.map((node, index) => {
          const label = node.name[locale] ?? node.name[locale === "ar" ? "en" : "ar"] ?? "";
          return (
            <li key={node.id} data-testid="tree-node">
              <div className="flex flex-wrap items-center gap-2 rounded-md bg-surface p-2">
                <span className={`font-semibold ${node.active ? "" : "text-ink-muted line-through"}`}>
                  <Localized text={node.name} locale={locale} />
                </span>
                {!node.active && <span className="rounded-full border border-line px-2 text-xs">{tTree("inactive")}</span>}
                {node.name.missing && node.name.missing.length > 0 && (
                  <span className="rounded-full border border-bad px-2 text-xs text-bad">{tTree("missingTranslation")}</span>
                )}
                {canManage && (
                  <span className="ms-auto flex flex-wrap gap-1">
                    {node.depth < maxTreeDepth && (
                      <button type="button" className={smallButton} disabled={busy} onClick={() => startEditing({ mode: "add", parentId: node.id })}>
                        {tTree("addChild", { name: label })}
                      </button>
                    )}
                    <button type="button" className={smallButton} disabled={busy} onClick={() => startEditing({ mode: "rename", node })}>
                      {tTree("rename", { name: label })}
                    </button>
                    <button type="button" className={smallButton} disabled={busy || index === 0}
                      onClick={() => void change(() => patchJson(`${base}/${node.id}`, { position: index - 1 }, csrfToken))}>
                      {tTree("moveUp", { name: label })}
                    </button>
                    <button type="button" className={smallButton} disabled={busy || index === level.length - 1}
                      onClick={() => void change(() => patchJson(`${base}/${node.id}`, { position: index + 1 }, csrfToken))}>
                      {tTree("moveDown", { name: label })}
                    </button>
                    <button type="button" className={smallButton} disabled={busy}
                      onClick={() => void change(() => patchJson(`${base}/${node.id}`, { active: !node.active }, csrfToken))}>
                      {node.active ? tTree("deactivate", { name: label }) : tTree("activate", { name: label })}
                    </button>
                    <button type="button" className={smallButton} disabled={busy}
                      onClick={() => void change(() => deleteRequest(`${base}/${node.id}`, csrfToken))}>
                      {tTree("delete", { name: label })}
                    </button>
                  </span>
                )}
              </div>
              {editing?.mode === "add" && editing.parentId === node.id && <div className="mt-2 ps-4">{form(tTree("childName", { name: label }))}</div>}
              {editing?.mode === "rename" && editing.node.id === node.id && <div className="mt-2">{form(tTree("newName", { name: label }))}</div>}
              {renderLevel(node.id)}
            </li>
          );
        })}
      </ul>
    );
  };

  return (
    <div className="space-y-4" data-testid={`tree-${kind}`}>
      {nodes.length === 0 ? <p>{t("empty")}</p> : renderLevel(null)}
      {canManage &&
        (editing?.mode === "add" && editing.parentId === null ? (
          form(t("addTop"))
        ) : (
          <button type="button" className={primaryButton} onClick={() => startEditing({ mode: "add", parentId: null })}>
            {t("addTop")}
          </button>
        ))}
      <SaveStatusLine status={status} />
    </div>
  );
}

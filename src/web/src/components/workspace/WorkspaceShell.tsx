"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import type { GroupSummary, TreeNode } from "@/lib/directory";
import { formatNumber, type DateDisplayOptions } from "@/lib/format";
import { levelName, type IncidentPage, type IncidentSettings, type Locale, type Person } from "@/lib/incidents";
import { useLive } from "@/lib/live";
import { filtersQuery, isTyping, shortcuts, type SavedView, type ViewFilters } from "@/lib/workspace";
import { MajorBadge, StateBadge } from "@/components/incidents/StateBadge";
import { secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { BulkBar } from "./BulkBar";
import { composeEvent } from "./Conversation";
import { SlaCountdownCell, useNow } from "./SlaCountdown";
import { ViewControls } from "./ViewControls";

export interface WorkspaceShellProps {
  page: IncidentPage | null;
  filters: ViewFilters;
  views: SavedView[];
  selectedId: string | null;
  settings: IncidentSettings | null;
  groups: GroupSummary[];
  categories: TreeNode[];
  locations: TreeNode[];
  assignees: Person[] | null;
  permissions: string[];
  csrfToken: string;
  display: DateDisplayOptions;
  /** The open incident (server-rendered), or a hint when none is open. */
  detail: ReactNode;
  /** The customer and the AI assistant beside it. */
  aside: ReactNode;
}

/** How long live changes are gathered before the page loads them (several changes, one load). */
const refreshDelayMs = 1500;

/**
 * The one-screen agent workspace (module 5): the incident list with views, filters and bulk
 * actions; the open incident with its conversation; the customer and the AI assistant beside it.
 * Choosing an incident or a view changes the address without reloading the page, and live updates
 * load what changed, so the screen stays current while the agent works. Keyboard shortcuts move
 * through the list and start replies; they do nothing while typing.
 */
export function WorkspaceShell(props: WorkspaceShellProps) {
  const { page, filters, views, selectedId, settings, groups, categories, locations, assignees, permissions, csrfToken, display, detail, aside } = props;
  const t = useTranslations("workspace");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const now = useNow();
  const [chosen, setSelected] = useState<string[]>([]);
  const [help, setHelp] = useState(false);
  const [liveNote, setLiveNote] = useState<string | null>(null);
  const refreshTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const items = useMemo(() => page?.items ?? [], [page]);
  const query = filtersQuery(filters, page?.page ?? 1).replace(/&?pageSize=\d+/, "");
  const numbers = useMemo(() => Object.fromEntries(items.map((i) => [i.id, i.number])), [items]);
  const canBulk = permissions.includes("incidents.edit");

  const open = useCallback((id: string | null) => {
    router.push(`/workspace${id ? `/${id}` : ""}?${query}`, { scroll: false });
  }, [router, query]);

  const showFilters = useCallback((filters: ViewFilters) => {
    // No filters at all is a choice too (every incident, newest first), not the default view.
    const next = Object.keys(filters).length > 0 ? filters : { sort: "created" as const };
    setSelected([]);
    router.replace(`/workspace${selectedId ? `/${selectedId}` : ""}?${filtersQuery(next).replace(/&?pageSize=\d+/, "")}`, { scroll: false });
  }, [router, selectedId]);

  const refreshSoon = useCallback(() => {
    if (refreshTimer.current) return;
    refreshTimer.current = setTimeout(() => {
      refreshTimer.current = null;
      if (document.visibilityState === "visible") router.refresh();
    }, refreshDelayMs);
  }, [router]);

  useEffect(() => () => { if (refreshTimer.current) clearTimeout(refreshTimer.current); }, []);

  const status = useLive(csrfToken, {
    ticketChanged: (payload) => {
      const ticketId = (payload as { ticketId?: string } | undefined)?.ticketId;
      if (ticketId && ticketId === selectedId) setLiveNote(t("live.changed"));
      refreshSoon();
    },
    refresh: refreshSoon,
  });

  // Selections count only incidents still in the list.
  const selected = useMemo(() => chosen.filter((id) => items.some((i) => i.id === id)), [chosen, items]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      if (event.key === "Escape") {
        setHelp(false);
        return;
      }
      if (isTyping(event.target)) return;
      const index = items.findIndex((i) => i.id === selectedId);
      switch (event.key) {
        case "j":
        case "k": {
          if (items.length === 0) return;
          const next = event.key === "j" ? Math.min(items.length - 1, index + 1) : Math.max(0, index < 0 ? 0 : index - 1);
          event.preventDefault();
          open(items[next]!.id);
          document.getElementById(`row-${items[next]!.id}`)?.focus();
          break;
        }
        case "x":
          if (selectedId && canBulk) {
            event.preventDefault();
            setSelected((current) => (current.includes(selectedId) ? current.filter((id) => id !== selectedId) : [...current, selectedId]));
          }
          break;
        case "r":
        case "n":
          if (selectedId) {
            event.preventDefault();
            window.dispatchEvent(new CustomEvent(composeEvent, { detail: event.key === "n" ? "Internal" : "Public" }));
          }
          break;
        case "a": {
          const target = document.querySelector<HTMLElement>("#workspace-work select, #workspace-work button");
          if (target) {
            event.preventDefault();
            target.focus();
          }
          break;
        }
        case "/":
          event.preventDefault();
          document.getElementById("workspace-view")?.focus();
          break;
        case "?":
          event.preventDefault();
          setHelp((h) => !h);
          break;
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [items, selectedId, open, canBulk]);

  const toggle = (id: string) => setSelected((current) => (current.includes(id) ? current.filter((x) => x !== id) : [...current, id]));
  const allSelected = items.length > 0 && items.every((i) => selected.includes(i.id));
  const number = (n: number) => formatNumber(n, display.locale, display.digits);
  const pages = page ? Math.max(1, Math.ceil(page.totalCount / page.pageSize)) : 1;
  const goToPage = (n: number) => router.replace(`/workspace${selectedId ? `/${selectedId}` : ""}?${filtersQuery(filters, n).replace(/&?pageSize=\d+/, "")}`, { scroll: false });

  return (
    <div data-layout="wide" className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-bold text-brand">{t("title")}</h1>
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <span className={status === "live" ? "text-ok" : "text-ink-muted"} data-testid="live-status">
            <span aria-hidden="true">{status === "live" ? "● " : "○ "}</span>
            {t(`live.${status}`)}
          </span>
          {liveNote && <span aria-live="polite" className="text-ink-muted">{liveNote}</span>}
          <button type="button" className={secondaryButtonClass} onClick={() => setHelp(true)} aria-haspopup="dialog">
            {t("shortcuts.open")}
          </button>
        </div>
      </div>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[20rem_minmax(0,1fr)] xl:grid-cols-[20rem_minmax(0,1fr)_18rem]">
        <section aria-labelledby="workspace-list-heading" className={`space-y-3 ${selectedId ? "hidden lg:block" : ""}`}>
          <h2 id="workspace-list-heading" className="sr-only">{t("list.heading")}</h2>
          <ViewControls filters={filters} views={views} settings={settings} groups={groups} csrfToken={csrfToken}
            permissions={permissions} onChange={showFilters} onViewsChanged={() => router.refresh()} />
          {canBulk && selected.length > 0 && (
            <BulkBar selected={selected} numbers={numbers} csrfToken={csrfToken} permissions={permissions} settings={settings}
              assignees={assignees} groups={groups} categories={categories} locations={locations}
              onDone={() => router.refresh()} onClear={() => setSelected([])} />
          )}
          {!page ? (
            <p role="alert">{t("list.unavailable")}</p>
          ) : (
            <>
              <div className="flex items-center justify-between gap-2 text-sm">
                <p className="text-ink-muted" aria-live="polite">{t("list.count", { count: page.totalCount, shown: number(page.totalCount) })}</p>
                {canBulk && items.length > 0 && (
                  <label className="flex items-center gap-2">
                    <input type="checkbox" checked={allSelected} onChange={() => setSelected(allSelected ? [] : items.map((i) => i.id))} />
                    {t("list.selectAll")}
                  </label>
                )}
              </div>
              {items.length === 0 ? (
                <p>{t("list.empty")}</p>
              ) : (
                <ul className="divide-y divide-line rounded-lg border border-line bg-surface" data-testid="workspace-list">
                  {items.map((incident) => {
                    const current = incident.id === selectedId;
                    return (
                      <li key={incident.id} className={`flex gap-2 p-2 ${current ? "bg-surface-muted" : ""}`}>
                        {canBulk && (
                          <input type="checkbox" className="mt-1" checked={selected.includes(incident.id)} onChange={() => toggle(incident.id)}
                            aria-label={t("list.select", { number: incident.number })} />
                        )}
                        <Link id={`row-${incident.id}`} href={`/workspace/${incident.id}?${query}`} scroll={false}
                          aria-current={current ? "true" : undefined}
                          className="block min-w-0 flex-1 space-y-1 rounded focus-visible:outline-2 focus-visible:outline-brand">
                          <span className="flex flex-wrap items-center gap-2 text-xs">
                            <span dir="ltr" className="font-mono text-ink-muted">{incident.number}</span>
                            <StateBadge state={incident.state} />
                            {incident.isMajor && <MajorBadge />}
                            {settings && incident.priority !== undefined && (
                              <span className="font-semibold">{levelName(settings.priorityMatrix.priorities, incident.priority, locale)}</span>
                            )}
                          </span>
                          <span className="block truncate font-semibold text-ink" dir="auto">{incident.title}</span>
                          <span className="flex flex-wrap items-center gap-2 text-xs text-ink-muted">
                            <SlaCountdownCell clocks={incident.sla} display={display} now={now} />
                            <span>{incident.assignee?.displayName ?? t("list.unassigned")}</span>
                          </span>
                        </Link>
                      </li>
                    );
                  })}
                </ul>
              )}
              {pages > 1 && (
                <nav aria-label={t("list.pages")} className="flex items-center justify-between text-sm">
                  <button type="button" className={secondaryButtonClass} disabled={page.page <= 1} onClick={() => goToPage(page.page - 1)}>{t("list.previous")}</button>
                  <span>{t("list.page", { page: number(page.page), pages: number(pages) })}</span>
                  <button type="button" className={secondaryButtonClass} disabled={page.page >= pages} onClick={() => goToPage(page.page + 1)}>{t("list.next")}</button>
                </nav>
              )}
            </>
          )}
        </section>

        <section aria-label={t("detail.label")} className={`min-w-0 space-y-4 ${selectedId ? "" : "hidden lg:block"}`}>
          {selectedId && (
            <Link href={`/workspace?${query}`} className="text-sm font-semibold text-brand underline lg:hidden">{t("detail.back")}</Link>
          )}
          {detail}
        </section>

        <aside aria-label={t("aside.label")} className={`space-y-4 lg:col-start-2 xl:col-start-3 ${selectedId ? "" : "hidden xl:block"}`}>
          {aside}
        </aside>
      </div>

      {help && <ShortcutsDialog onClose={() => setHelp(false)} />}
    </div>
  );
}

/** The keyboard shortcuts, in a dialog that keeps focus until closed. */
function ShortcutsDialog({ onClose }: { onClose: () => void }) {
  const t = useTranslations("workspace.shortcuts");
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const element = dialog.current;
    if (element && !element.open) element.showModal?.();
    return () => element?.close?.();
  }, []);
  return (
    <dialog ref={dialog} onClose={onClose} aria-labelledby="shortcuts-heading" className="rounded-lg border border-line bg-surface p-4 text-ink">
      <h2 id="shortcuts-heading" className="mb-3 text-xl font-bold">{t("title")}</h2>
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2">
        {shortcuts.map((s) => (
          <div key={s.keys} className="contents">
            <dt><kbd className="rounded border border-line px-2 py-0.5 font-mono" dir="ltr">{s.keys === "Escape" ? "Esc" : s.keys}</kbd></dt>
            <dd>{t(`actions.${s.action}`)}</dd>
          </div>
        ))}
      </dl>
      <button type="button" onClick={onClose} className="mt-4 rounded-md bg-brand px-4 py-2 font-semibold text-surface" autoFocus>{t("close")}</button>
    </dialog>
  );
}

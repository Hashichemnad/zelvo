"use client";

import { useId, useState, type ReactNode } from "react";
import { usePathname } from "next/navigation";

/**
 * Task J1 (phone layouts): on narrow screens the header's links fold behind a "Menu" button, so a
 * phone shows the page first; from the `sm` width up they are always shown. The button says
 * whether the menu is open (aria-expanded) and the menu closes after moving to another page.
 */
export function CollapsibleNav({ label, menuLabel, children }: { label: string; menuLabel: string; children: ReactNode }) {
  const id = useId();
  const pathname = usePathname();
  // Open for the page it was opened on only, so moving to another page closes it.
  const [openOn, setOpenOn] = useState<string | null>(null);
  const open = openOn === pathname;
  const setOpen = (toggle: (o: boolean) => boolean) => setOpenOn(toggle(open) ? pathname : null);
  return (
    <>
      <button type="button" aria-expanded={open} aria-controls={id} onClick={() => setOpen((o) => !o)}
        className="rounded-md border border-line px-3 py-1.5 text-sm font-semibold sm:hidden" data-testid="menu-button">
        {menuLabel}
      </button>
      <nav id={id} aria-label={label} className={`${open ? "block" : "hidden"} w-full sm:block sm:w-auto`}>
        {children}
      </nav>
    </>
  );
}

"use client";

import { fieldButton } from "@/components/forms/ui";

/** Prints the page (the receipt). Hidden when printing. */
export function PrintButton({ label }: { label: string }) {
  return <button type="button" className={`${fieldButton} print:hidden`} onClick={() => window.print()}>{label}</button>;
}

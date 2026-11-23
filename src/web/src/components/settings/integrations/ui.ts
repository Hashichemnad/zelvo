/** Shared classes of the Integrations screens (logical properties only, for both directions). */
export const buttonClass =
  "rounded-md px-4 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60";
export const primaryButton = `${buttonClass} bg-brand text-surface`;
export const secondaryButton = `${buttonClass} border border-line text-ink`;
export const dangerButton = `${buttonClass} border border-bad text-bad`;
export const inputClass =
  "block w-full rounded-md border border-line bg-surface px-3 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";
export const tableClass = "w-full border-collapse text-start text-sm";
export const cellClass = "border-b border-line px-2 py-2 text-start align-top";
/** Technical text (URLs, keys, JSON) is shown left to right in both languages. */
export const codeClass = "block max-w-full overflow-x-auto whitespace-pre-wrap break-all rounded bg-surface-muted px-2 py-1 text-sm";

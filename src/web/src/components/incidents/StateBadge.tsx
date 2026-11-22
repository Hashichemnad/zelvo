import { useTranslations } from "next-intl";
import type { TicketState } from "@/lib/incidents";

const tone: Record<TicketState, string> = {
  New: "border-brand text-brand",
  Assigned: "border-brand text-brand",
  InProgress: "border-brand text-brand",
  OnHold: "border-warn text-warn",
  Resolved: "border-ok text-ok",
  Closed: "border-line text-ink-muted",
  Reopened: "border-warn text-warn",
};

/** An incident's state as a labelled badge (text, not colour alone, carries the meaning). */
export function StateBadge({ state }: { state: TicketState }) {
  const t = useTranslations("incidents.states");
  return <span className={`inline-block rounded-full border px-2 py-0.5 text-xs font-semibold ${tone[state]}`}>{t(state)}</span>;
}

/** The "major incident" badge. */
export function MajorBadge() {
  const t = useTranslations("incidents.fields");
  return <span className="inline-block rounded-full bg-bad px-2 py-0.5 text-xs font-semibold text-surface">{t("major")}</span>;
}

/** Text that may be in the other language: marked so screen readers pronounce it right. */
export function Localized({ value, lang, dir }: { value: string; lang?: string; dir?: "ltr" | "rtl" }) {
  return lang ? (
    <span lang={lang} dir={dir}>
      {value}
    </span>
  ) : (
    <>{value}</>
  );
}

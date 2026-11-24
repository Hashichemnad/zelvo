"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { putJson } from "@/lib/client-api";
import { SaveStatusLine, useSaver } from "./common";

/** "Available for assignment" on the Account page: round-robin skips people who switch it off. */
export function AvailabilitySwitch({ initial, csrfToken }: { initial: boolean; csrfToken: string }) {
  const t = useTranslations("account.availability");
  const id = useId();
  const [available, setAvailable] = useState(initial);
  const { status, run, busy } = useSaver();

  return (
    <div className="space-y-2">
      <label className="flex items-center gap-2">
        <input
          type="checkbox"
          role="switch"
          aria-checked={available}
          aria-describedby={`${id}-hint`}
          checked={available}
          disabled={busy}
          onChange={(e) => {
            const next = e.target.checked;
            void run(async () => {
              await putJson("/v1/me/availability", { available: next }, csrfToken);
              setAvailable(next);
            });
          }}
        />
        <span className="font-semibold">{t("label")}</span>
      </label>
      <p id={`${id}-hint`} className="max-w-2xl text-sm text-ink-muted">{t("hint")}</p>
      <SaveStatusLine status={status} />
    </div>
  );
}

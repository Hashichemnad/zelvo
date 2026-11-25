"use client";

import { useEffect, useId, useState } from "react";
import { useTranslations } from "next-intl";

/**
 * Task F5: whether the organisation published a privacy notice, asked once through the proxy
 * (open to visitors). Null while unknown; on any failure the API decides (it refuses without the
 * acceptance when a notice exists, so nothing is sent without it).
 */
export function usePrivacyNoticePublished(enabled = true): boolean | null {
  const [published, setPublished] = useState<boolean | null>(null);
  useEffect(() => {
    if (!enabled) return;
    let live = true;
    fetch("/bff/api/v1/privacy/notice", { headers: { Accept: "application/json" }, cache: "no-store" })
      .then(async (response) => {
        const body = response.ok ? ((await response.json().catch(() => null)) as { version?: unknown } | null) : null;
        if (live) setPublished(typeof body?.version === "number");
      })
      .catch(() => live && setPublished(false));
    return () => {
      live = false;
    };
  }, [enabled]);
  return enabled ? published : false;
}

/** "I have read the privacy notice", with a link opening the notice in a new tab; required. */
export function PrivacyNoticeCheckbox({ locale, checked, onChange }: { locale: string; checked: boolean; onChange: (checked: boolean) => void }) {
  const t = useTranslations("privacy.consent");
  const id = useId();
  return (
    <div className="flex items-start gap-2" data-testid="privacy-notice-checkbox">
      <input id={id} type="checkbox" required checked={checked} onChange={(e) => onChange(e.target.checked)} className="mt-1 size-4" />
      <label htmlFor={id}>
        {t.rich("checkbox", {
          link: (chunks) => (
            <a href={`/${locale}/privacy`} target="_blank" rel="noopener noreferrer" className="font-semibold text-brand underline">
              {chunks}
            </a>
          ),
        })}
      </label>
    </div>
  );
}

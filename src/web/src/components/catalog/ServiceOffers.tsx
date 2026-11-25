"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { Localized } from "@/components/directory/common";
import { fieldButton } from "@/components/forms/ui";
import { postJson } from "@/lib/client-api";
import { formatLongDate, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import type { ServiceOffer } from "@/lib/catalog";

/**
 * Services Zelvo offers the person (task C5, proactive services): each opens the service's form
 * filled in with what Zelvo knows, or can be declined. Shown on My tickets.
 */
export function ServiceOffers({ offers, display, csrfToken }: { offers: ServiceOffer[]; display: DateDisplayOptions; csrfToken: string }) {
  const t = useTranslations("catalog.offers");
  const locale = useLocale() as Locale;
  const [items, setItems] = useState(offers);
  const [message, setMessage] = useState("");
  if (offers.length === 0) return null;
  return (
    <section aria-labelledby="service-offers" className="space-y-3 rounded-lg border border-brand p-4" data-testid="service-offers">
      <h2 id="service-offers" className="text-xl font-semibold">{t("title")}</h2>
      <p>{t("intro")}</p>
      <ul className="space-y-3">
        {items.map((offer) => (
          <li key={offer.id} className="flex flex-wrap items-center justify-between gap-3 border-t border-line pt-3" data-testid="service-offer">
            <div className="space-y-1">
              <p className="font-semibold"><Localized text={offer.service.name} locale={locale} />{offer.kind === "Bundle" && <span className="ms-2 text-sm font-normal text-ink-muted">({t("bundle")})</span>}</p>
              <p className="text-sm"><Localized text={offer.summary} locale={locale} /></p>
              {offer.expiresAt && <p className="text-sm text-ink-muted">{t("expires", { date: formatLongDate(new Date(offer.expiresAt), display) })}</p>}
            </div>
            <div className="flex flex-wrap gap-2">
              <Link href={`/catalog/${offer.service.id}/request?offer=${offer.id}`} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("open")}</Link>
              <button type="button" className={fieldButton}
                onClick={() => void postJson(`/v1/me/offers/${offer.id}/decline`, undefined, csrfToken)
                  .then(() => { setItems((list) => list.filter((x) => x.id !== offer.id)); setMessage(t("declined")); })
                  .catch(() => setMessage(t("failed")))}>
                {t("decline")}
              </button>
            </div>
          </li>
        ))}
      </ul>
      <p role="status" aria-live="polite" className="text-sm">{message}</p>
    </section>
  );
}

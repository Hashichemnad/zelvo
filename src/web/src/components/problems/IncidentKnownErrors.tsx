"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { smallButton } from "@/components/directory/common";
import { Link } from "@/i18n/navigation";
import { getJson } from "@/lib/client-api";
import { insertIntoReply, type MatchingKnownError, type Problem } from "@/lib/problems";

/**
 * Known errors matching an incident (task H1), on the staff incident page and in the workspace:
 * the workaround in each language it has, why it matches, and a button that pastes it into the
 * workspace's reply (or copies it on the incident page). Holders of problems.read also see the
 * problems the incident is linked to, and problems.edit raises one from it.
 */
export function IncidentKnownErrors({ incidentId, canInsert, canSeeProblems, canRaise }: {
  incidentId: string;
  /** The workspace's composer is on the page: "Insert into reply". Elsewhere the text is copied. */
  canInsert: boolean;
  canSeeProblems: boolean;
  canRaise: boolean;
}) {
  const t = useTranslations("problems.incidentPanel");
  const tLanguages = useTranslations("display.languages");
  const tStages = useTranslations("problems.stages");
  const locale = useLocale();
  const id = useId();
  const [matches, setMatches] = useState<MatchingKnownError[] | null>(null);
  const [problems, setProblems] = useState<Problem[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    let live = true;
    getJson<MatchingKnownError[]>(`/v1/incidents/${incidentId}/known-errors`)
      .then((list) => live && setMatches(list))
      .catch(() => live && setFailed(true));
    if (canSeeProblems) {
      getJson<Problem[]>(`/v1/incidents/${incidentId}/problems`)
        .then((list) => live && setProblems(list))
        .catch(() => live && setProblems([]));
    }
    return () => {
      live = false;
    };
  }, [incidentId, canSeeProblems]);

  async function use(text: string, language: "en" | "ar") {
    if (canInsert) {
      insertIntoReply({ incidentId, text, language });
      return;
    }
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="incident-known-errors">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {failed && <p role="alert">{t("failed")}</p>}
      {matches && matches.length === 0 && <p>{t("none")}</p>}
      {matches && matches.length > 0 && (
        <ul className="space-y-3">
          {matches.map(({ knownError: e, reasons }) => (
            <li key={e.id} className="space-y-2 rounded-md border border-line p-3">
              <p className="font-semibold">
                <span className="font-mono" dir="ltr">{e.number}</span>{" "}
                {(["en", "ar"] as const).filter((l) => e.title[l]).slice(0, 1).map((l) => <span key={l} lang={l} dir={l === "ar" ? "rtl" : "ltr"}>{e.title[l]}</span>)}
              </p>
              <p className="text-sm text-ink-muted">{t("matches", { reasons: reasons.map((r) => t(`reasons.${r}`)).join(locale === "ar" ? "، " : ", ") })}</p>
              {(["en", "ar"] as const).filter((l) => e.workaround[l]).map((l) => (
                <div key={l} className="space-y-1">
                  <p className="whitespace-pre-wrap" lang={l} dir={l === "ar" ? "rtl" : "ltr"}>{e.workaround[l]}</p>
                  <button type="button" className={smallButton} onClick={() => void use(e.workaround[l]!, l)}>
                    {canInsert ? t("insert", { language: tLanguages(l) }) : t("copy", { language: tLanguages(l) })}
                  </button>
                </div>
              ))}
            </li>
          ))}
        </ul>
      )}
      {copied && <p role="status">{t("copied")}</p>}
      {canSeeProblems && problems && (
        <div className="space-y-1">
          <h3 className="font-semibold">{t("problems")}</h3>
          {problems.length === 0 ? <p className="text-ink-muted">{t("noProblems")}</p> : (
            <ul>
              {problems.map((p) => (
                <li key={p.id}>
                  <Link href={`/problems/${p.id}`} className="text-brand underline-offset-4 hover:underline">{t("openProblem", { number: p.number })}</Link>{" "}
                  <span dir="auto">{p.title}</span> <span className="text-sm text-ink-muted">({tStages(p.stage)})</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {canRaise && (
        <Link href={`/problems/new?incidents=${incidentId}`} className="inline-block font-semibold text-brand underline-offset-4 hover:underline">{t("raise")}</Link>
      )}
    </section>
  );
}

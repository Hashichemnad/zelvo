"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { SaveStatusLine, useSaver } from "@/components/directory/common";
import { buttonClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { Link, useRouter } from "@/i18n/navigation";
import type { AppImport } from "@/lib/apps";
import { postJson } from "@/lib/client-api";
import { inLanguage, type Locale } from "@/lib/incidents";

/**
 * The preview of an imported app package (task K3, decided 2026-11-26): what is wrong with it,
 * what already exists here (keep or replace, nothing changes without a choice), what it adds, the
 * permissions it makes and grants, and what its references were found as. Nothing of it is active
 * until an administrator approves it.
 */
export function ImportReview({ item, csrfToken }: { item: AppImport; csrfToken: string }) {
  const t = useTranslations("apps.review");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const plan = item.plan;
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [confirmed, setConfirmed] = useState(false);
  const allChosen = !!plan && plan.conflicts.every((c) => choices[c.id] === "keep" || choices[c.id] === "replace");

  function decide(action: "approve" | "discard") {
    void run(async () => {
      await postJson(`/v1/app-imports/${item.id}/${action}`, action === "approve" ? { choices } : undefined, csrfToken);
      router.refresh();
    });
  }

  if (item.status !== "Pending" || !plan) {
    return (
      <div className="space-y-3" data-testid="import-decided">
        <p className="font-semibold">{t(`decided.${item.status}`)}</p>
        {item.result && (
          <ul className="list-disc space-y-1 ps-6 text-sm">
            <li>{t("added", { count: item.result.added.length })}</li>
            <li>{t("replaced", { count: item.result.replaced.length })}</li>
            <li>{t("kept", { count: item.result.kept.length })}</li>
          </ul>
        )}
        {item.status === "Approved" && <Link href={`/settings/apps/${item.appKey}`} className="font-semibold text-brand underline-offset-4 hover:underline">{t("openApp")}</Link>}
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <dl className="grid max-w-2xl gap-x-6 gap-y-1 sm:grid-cols-2">
        <dt className="font-semibold">{t("app")}</dt>
        <dd>{inLanguage(plan.appName, locale).value} <span dir="ltr" className="font-mono text-sm">({plan.appKey})</span></dd>
        {item.fileName && (<><dt className="font-semibold">{t("file")}</dt><dd dir="ltr">{item.fileName}</dd></>)}
        {plan.source?.profile && (<><dt className="font-semibold">{t("source")}</dt><dd dir="ltr">{plan.source.profile} {plan.source.productVersion}</dd></>)}
        <dt className="font-semibold">{t("fingerprint")}</dt>
        <dd dir="ltr" className="break-all font-mono text-xs">{item.sha256}</dd>
      </dl>

      <section aria-labelledby={`${id}-problems`} className="space-y-2">
        <h3 id={`${id}-problems`} className="text-lg font-semibold">{t("problems")}</h3>
        {plan.problems.length === 0 ? <p className="text-ok">{t("noProblems")}</p> : (
          <ul role="list" className="list-disc space-y-1 ps-6 text-bad" data-testid="import-problems">
            {plan.problems.map((p, i) => (
              <li key={i}>{t.has(`codes.${p.code}`) ? t(`codes.${p.code}`) : p.message} <span dir="ltr" className="font-mono text-xs text-ink-muted">{p.path}</span></li>
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby={`${id}-adds`} className="space-y-2">
        <h3 id={`${id}-adds`} className="text-lg font-semibold">{t("contents")}</h3>
        <ul className="list-disc space-y-1 ps-6">
          {plan.items.map((i) => (
            <li key={`${i.kind}:${i.key}`}>
              {t(`kinds.${i.kind}`)}: {inLanguage(i.name, locale).value} <span dir="ltr" className="font-mono text-sm">({i.key})</span>
              {" "}{i.exists ? t("exists") : t("new")}
            </li>
          ))}
        </ul>
        <p className="text-sm">{t("counts", { forms: plan.forms, rules: plan.rules, views: plan.listViews, menu: plan.menuEntries, portal: plan.portalEntries })}</p>
      </section>

      <section aria-labelledby={`${id}-permissions`} className="space-y-2">
        <h3 id={`${id}-permissions`} className="text-lg font-semibold">{t("permissions")}</h3>
        {plan.permissionsCreated.length > 0 && <p>{t("created")}: <span dir="ltr" className="font-mono text-sm">{plan.permissionsCreated.join(", ")}</span></p>}
        {plan.grants.length === 0 ? <p>{t("noGrants")}</p> : (
          <ul className="list-disc space-y-1 ps-6">
            {plan.grants.map((g) => (
              <li key={g.role}>{t(g.roleExists ? "grantExisting" : "grantNew", { role: g.role })}: <span dir="ltr" className="font-mono text-sm">{g.permissions.join(", ")}</span></li>
            ))}
          </ul>
        )}
      </section>

      {plan.references.length > 0 && (
        <section aria-labelledby={`${id}-references`} className="space-y-2">
          <h3 id={`${id}-references`} className="text-lg font-semibold">{t("references")}</h3>
          <ul className="list-disc space-y-1 ps-6">
            {plan.references.map((r, i) => (
              <li key={i} className={r.foundId ? "" : "text-bad"}>
                {t(`referenceKinds.${r.kind}`)}: <bdi>{r.label}</bdi> — {r.foundId ? t("foundAs", { name: inLanguage(r.foundName, locale).value }) : t("notFound")}
              </li>
            ))}
          </ul>
        </section>
      )}

      {plan.conflicts.length > 0 && (
        <fieldset className="space-y-3">
          <legend className="text-lg font-semibold">{t("conflicts")}</legend>
          <p className="text-sm">{t("conflictsHint")}</p>
          {plan.conflicts.map((c) => (
            <div key={c.id} role="radiogroup" aria-labelledby={`${id}-${c.id}`} className="space-y-1 rounded-md border border-line p-3">
              <p id={`${id}-${c.id}`} className="font-semibold">
                {t(`kinds.${c.kind}`)}: {inLanguage(c.existing, locale).value} <span dir="ltr" className="font-mono text-sm">({c.key})</span>
              </p>
              <p className="text-sm text-ink-muted">{t(`replaceMeans.${c.kind}`)}</p>
              <label className="flex items-center gap-2">
                <input type="radio" name={c.id} className="size-5 accent-brand" checked={choices[c.id] === "keep"} onChange={() => setChoices({ ...choices, [c.id]: "keep" })} />
                {t("keep")}
              </label>
              <label className="flex items-center gap-2">
                <input type="radio" name={c.id} className="size-5 accent-brand" checked={choices[c.id] === "replace"} onChange={() => setChoices({ ...choices, [c.id]: "replace" })} />
                {t("replace")}
              </label>
            </div>
          ))}
        </fieldset>
      )}

      <div className="space-y-3 rounded-lg border border-line p-4">
        <label className="flex items-start gap-2">
          <input type="checkbox" className="mt-1 size-5 accent-brand" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
          <span>{t("confirm")}</span>
        </label>
        <div className="flex flex-wrap gap-2">
          <button type="button" className={buttonClass} disabled={busy || !plan.valid || !allChosen || !confirmed} onClick={() => decide("approve")}>{t("approve")}</button>
          <button type="button" className={secondaryButtonClass} disabled={busy} onClick={() => decide("discard")}>{t("discard")}</button>
        </div>
        {!plan.valid && <p className="text-sm">{t("fixFirst")}</p>}
        {plan.valid && !allChosen && <p className="text-sm">{t("chooseFirst")}</p>}
      </div>
      <SaveStatusLine status={status} />
    </div>
  );
}

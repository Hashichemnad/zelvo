"use client";

import { useId, useState, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { smallButton, useSaver } from "@/components/directory/common";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { MarkdownView } from "@/components/knowledge/MarkdownView";
import { ApiError, deleteRequest, getJson, patchJson, postFile, postJson, putJson } from "@/lib/client-api";
import { textOf, type Department, type GroupSummary, type Role, type TreeNode } from "@/lib/directory";
import type { FormSummary } from "@/lib/forms/types";
import type { Locale } from "@/lib/incidents";
import {
  accessModes, cardForApi, completeCard, deliveryChannels, emptyText, partnerKinds, serviceIcons, stepStages, targetAudiences,
  type Catalog, type ServiceAction, type ServiceCard, type ServiceDetail, type ServiceSummary, type ServiceVersion, type Text,
} from "@/lib/catalog";
import { CatalogStatusLine, ServiceStateBadge } from "./common";
import { PeopleList } from "./PeopleList";
import { ApprovalLevelsEditor } from "@/components/workflows/WorkflowEditors";
import { FulfilmentEditor } from "./FulfilmentEditor";

export interface ServiceEditorChoices {
  services: ServiceSummary[];
  catalogs: Catalog[];
  categories: TreeNode[];
  locations: TreeNode[];
  departments: Department[];
  groups: GroupSummary[];
  roles: Role[];
  /** Published forms to choose from; null without forms.manage. */
  forms: FormSummary[] | null;
}

export interface ServiceEditorProps {
  initial: ServiceDetail;
  choices: ServiceEditorChoices;
  csrfToken: string;
}

/**
 * The service editor (task C2): the whole service card in sections, saved as the working version
 * with If-Match, and the lifecycle actions the API allows (`actions`). Requesters keep the
 * published card until a reviewer other than the author approves the working one.
 */
export function ServiceEditor({ initial, choices, csrfToken }: ServiceEditorProps) {
  const t = useTranslations("catalog.editor");
  const tProblems = useTranslations("catalog.problems");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [detail, setDetail] = useState(initial);
  const [card, setCard] = useState<ServiceCard>(() => completeCard(initial.working?.card ?? initial.published?.card));
  const [changeNote, setChangeNote] = useState("");
  const [code, setCode] = useState(initial.code);
  const [dirty, setDirty] = useState(false);
  const [panel, setPanel] = useState<"sendBack" | "suspend" | "copy" | "template" | null>(null);
  const [versions, setVersions] = useState<ServiceVersion[] | null>(null);
  const { status, run, busy } = useSaver();
  const can = (action: ServiceAction) => detail.actions.includes(action);
  const editable = can("edit");
  const base = `/v1/catalog/services/${detail.id}`;
  const update = (patch: Partial<ServiceCard>) => {
    setCard((c) => ({ ...c, ...patch }));
    setDirty(true);
  };

  function loaded(next: ServiceDetail) {
    setDetail(next);
    setCard(completeCard(next.working?.card ?? next.published?.card));
    setDirty(false);
    setCode(next.code);
  }

  async function act(action: () => Promise<ServiceDetail | void>) {
    const result = await run(async () => {
      try {
        return await action();
      } catch (error) {
        if (error instanceof ApiError && error.code === "version-mismatch") {
          loaded(await getJson<ServiceDetail>(base));
        }
        throw error;
      }
    });
    if (result) loaded(result);
  }

  const save = () =>
    act(async () => {
      if (code !== detail.code) await patchJson(base, { code: code.trim().toUpperCase() }, csrfToken);
      return putJson<ServiceDetail>(`${base}/draft`, { card: cardForApi(card), changeNote: changeNote.trim() || undefined }, csrfToken, { "If-Match": detail.etag });
    });
  const post = (path: string, body?: unknown) => act(() => postJson<ServiceDetail>(`${base}/${path}`, body, csrfToken, { "If-Match": detail.etag }));
  const pending = detail.working !== null && detail.published !== null;
  const neverPublished = detail.published === null;

  return (
    <div className="space-y-6" data-testid="service-editor">
      <header className="space-y-2">
        <p className="font-mono text-ink-muted" dir="ltr">{detail.code}</p>
        <h2 className="text-2xl font-bold" dir="auto">{textOf(card.name, locale) || detail.code}</h2>
        <div className="flex flex-wrap items-center gap-3">
          <ServiceStateBadge state={detail.state} />
          {detail.published && <span className="text-sm">{t("publishedVersion", { number: detail.published.number })}</span>}
          {detail.working && (
            <span className="text-sm text-ink-muted">
              {t(detail.working.state === "UnderReview" ? "workingInReview" : "workingDraft", { number: detail.working.number })}
            </span>
          )}
          {detail.requestCount > 0 && <span className="text-sm text-ink-muted">{t("requestCount", { count: detail.requestCount })}</span>}
        </div>
        {detail.working?.reviewComment && (
          <p role="note" className="rounded-md border border-warn p-2"><span className="font-semibold">{t("reviewComment")}</span> <span dir="auto">{detail.working.reviewComment}</span></p>
        )}
        {detail.state === "Suspended" && <p role="note" className="text-warn" dir="auto">{textOf(detail.suspendedMessage, locale)}</p>}
      </header>

      <div className="flex flex-wrap items-center gap-2 rounded-lg border border-line p-3" role="toolbar" aria-label={t("toolbar")}>
        {editable && <button type="button" className={primaryAction} disabled={busy || !dirty} onClick={() => void save()}>{t("save")}</button>}
        {can("submit") && <button type="button" className={fieldButton} disabled={busy || dirty} onClick={() => void post("submit")}>{t("submit")}</button>}
        {can("approve") && <button type="button" className={fieldButton} disabled={busy} onClick={() => void post("approve")}>{t("approve")}</button>}
        {can("sendBack") && <button type="button" className={fieldButton} disabled={busy} onClick={() => setPanel("sendBack")}>{t("sendBack")}</button>}
        {can("discard") && (
          <button type="button" className={fieldButton} disabled={busy}
            onClick={() => void act(async () => {
              if (neverPublished && detail.state !== "Retired") {
                await postJson(`${base}/discard`, undefined, csrfToken);
                router.push("/settings/catalog");
                return;
              }
              return postJson<ServiceDetail>(`${base}/discard`, undefined, csrfToken);
            })}>{t("discard")}</button>
        )}
        {can("suspend") && <button type="button" className={fieldButton} disabled={busy} onClick={() => setPanel("suspend")}>{t("suspend")}</button>}
        {can("resume") && <button type="button" className={fieldButton} disabled={busy} onClick={() => void post("resume")}>{t("resume")}</button>}
        {can("retire") && <button type="button" className={fieldButton} disabled={busy} onClick={() => void post("retire")}>{t("retire")}</button>}
        {can("reopen") && <button type="button" className={fieldButton} disabled={busy} onClick={() => void post("reopen")}>{t("reopen")}</button>}
        {can("copy") && <button type="button" className={fieldButton} disabled={busy} onClick={() => setPanel("copy")}>{t("copy")}</button>}
        {can("saveAsTemplate") && <button type="button" className={fieldButton} disabled={busy} onClick={() => setPanel("template")}>{t("saveAsTemplate")}</button>}
        {can("delete") && (
          <button type="button" className={fieldButton} disabled={busy}
            onClick={() => void act(async () => { await deleteRequest(base, csrfToken); router.push("/settings/catalog"); })}>{t("delete")}</button>
        )}
        <CatalogStatusLine status={status} />
      </div>

      {panel && (
        <ActionPanel kind={panel} busy={busy} onCancel={() => setPanel(null)}
          onSubmit={async (value) => {
            if (panel === "sendBack") await post("send-back", { comment: value.text });
            else if (panel === "suspend") await post("suspend", { message: { en: value.en || null, ar: value.ar || null } });
            else if (panel === "copy") {
              const copy = await run(() => postJson<ServiceDetail>(`${base}/copy`, { code: value.code.toUpperCase(), name: value.en || value.ar ? { en: value.en || null, ar: value.ar || null } : undefined }, csrfToken));
              if (copy) router.push(`/settings/catalog/services/${copy.id}`);
            } else {
              await run(() => postJson(`${base}/save-as-template`, { name: { en: value.en || null, ar: value.ar || null } }, csrfToken));
            }
            setPanel(null);
          }} />
      )}

      {detail.problems.length > 0 && (
        <section aria-labelledby={`${id}-problems`} className="rounded-lg border border-bad p-3" data-testid="service-problems">
          <h3 id={`${id}-problems`} className="font-semibold text-bad">{t("problems", { count: detail.problems.length })}</h3>
          <ul className="list-disc space-y-1 ps-6">
            {detail.problems.map((p, i) => (
              <li key={`${p.field}-${p.code}-${i}`}>
                {tProblems.has(p.code) ? tProblems(p.code) : p.message}
                {p.field && <span className="ms-2 font-mono text-sm text-ink-muted" dir="ltr">{p.field}</span>}
              </li>
            ))}
          </ul>
        </section>
      )}

      {pending && detail.published?.card && (
        <details className="rounded-lg border border-line p-3">
          <summary className="cursor-pointer font-semibold">{t("publishedCard", { number: detail.published.number })}</summary>
          <div className="mt-2 space-y-2">
            <p className="font-semibold" dir="auto">{textOf(detail.published.card.name, locale)}</p>
            <p dir="auto">{textOf(detail.published.card.summary, locale)}</p>
            {textOf(detail.published.card.description, locale) && <MarkdownView markdown={textOf(detail.published.card.description, locale)} articleId={null} />}
          </div>
        </details>
      )}

      <fieldset disabled={!editable} className="space-y-6">
        <legend className="sr-only">{t("card")}</legend>
        <Section title={t("basics")}>
          {neverPublished && (
            <div className="space-y-1">
              <label htmlFor={`${id}-code`} className="block text-sm font-semibold">{t("code")}</label>
              <input id={`${id}-code`} value={code} onChange={(e) => { setCode(e.target.value); setDirty(true); }} maxLength={30} dir="ltr" className={`${fieldInput} font-mono uppercase`} />
            </div>
          )}
          <TextPair id={`${id}-name`} label={t("name")} value={card.name} max={200} onChange={(name) => update({ name })} />
          <TextPair id={`${id}-summary`} label={t("summary")} value={card.summary} max={300} onChange={(summary) => update({ summary })} />
          <TextPair id={`${id}-description`} label={t("description")} hint={t("descriptionHint")} value={card.description} max={20000} multiline onChange={(description) => update({ description })} />
          <div className="grid gap-3 sm:grid-cols-2">
            <Select id={`${id}-parent`} label={t("parent")} value={card.parentId ?? ""} onChange={(v) => update({ parentId: v || null })}
              options={choices.services.filter((s) => s.id !== detail.id && s.state !== "Retired").map((s) => ({ value: s.id, label: `${s.code} · ${textOf(s.name, locale)}` }))} none={t("none")} />
            <Select id={`${id}-department`} label={t("department")} value={card.departmentId ?? ""} onChange={(v) => update({ departmentId: v || null })}
              options={choices.departments.map((d) => ({ value: d.id, label: textOf(d.name, locale) }))} none={t("none")} />
            <Select id={`${id}-type`} label={t("type")} value={card.type} onChange={(v) => update({ type: v as ServiceCard["type"] })}
              options={[{ value: "Internal", label: t("types.Internal") }, { value: "External", label: t("types.External") }]} />
            {card.type === "External" && (
              <div className="space-y-1">
                <label htmlFor={`${id}-url`} className="block text-sm font-semibold">{t("externalUrl")}</label>
                <input id={`${id}-url`} type="url" dir="ltr" value={card.externalUrl ?? ""} onChange={(e) => update({ externalUrl: e.target.value })} className={fieldInput} />
              </div>
            )}
            <Select id={`${id}-icon`} label={t("icon")} value={card.icon ?? ""} onChange={(v) => update({ icon: v || null })}
              options={serviceIcons.map((i) => ({ value: i, label: t.has(`icons.${i}`) ? t(`icons.${i}`) : i }))} none={t("none")} />
            <OwnerPicker id={`${id}-owner`} ownerId={card.ownerId} onChange={(ownerId) => update({ ownerId })} />
          </div>
          <CheckList legend={t("catalogs")} items={choices.catalogs.map((c) => ({ value: c.id, label: textOf(c.name, locale) }))} selected={card.catalogIds} onChange={(catalogIds) => update({ catalogIds })} />
          <CheckList legend={t("categories")} items={choices.categories.filter((c) => c.selectable).map((c) => ({ value: c.id, label: textOf(c.path, locale) || textOf(c.name, locale) }))}
            selected={card.categoryIds} onChange={(categoryIds) => update({ categoryIds })} />
          <ImagePicker serviceId={detail.id} files={detail.files} imageFileId={card.imageFileId} csrfToken={csrfToken}
            onChange={(imageFileId) => update({ imageFileId })} onUploaded={(next) => setDetail((d) => ({ ...d, files: next.files }))} />
        </Section>

        <Section title={t("form")}>
          {choices.forms ? (
            <Select id={`${id}-form`} label={t("formLabel")} value={card.formId ?? ""} onChange={(v) => update({ formId: v || null })}
              options={choices.forms.filter((f) => f.state !== "Retired").map((f) => ({ value: f.id, label: `${textOf(f.name, locale)}${f.state === "Draft" ? ` (${t("formDraft")})` : ""}` }))} none={t("noForm")} />
          ) : (
            <p className="text-ink-muted">{card.formId ? t("formChosen") : t("noForm")}</p>
          )}
        </Section>

        <Section title={t("documents")}>
          <ListEditor items={card.documents} onChange={(documents) => update({ documents })} addLabel={t("addDocument")}
            blank={() => ({ key: "", name: emptyText(), description: emptyText(), required: true, fileTypes: [], maxSizeMb: null, templateFileId: null })}
            render={(doc, i, set) => (
              <div className="space-y-2">
                <div className="grid gap-2 sm:grid-cols-3">
                  <div className="space-y-1">
                    <label htmlFor={`${id}-doc-${i}-key`} className="block text-sm font-semibold">{t("documentKey")}</label>
                    <input id={`${id}-doc-${i}-key`} value={doc.key} dir="ltr" maxLength={41} onChange={(e) => set({ ...doc, key: e.target.value })} className={`${fieldInput} font-mono`} />
                  </div>
                  <div className="space-y-1">
                    <label htmlFor={`${id}-doc-${i}-types`} className="block text-sm font-semibold">{t("fileTypes")}</label>
                    <input id={`${id}-doc-${i}-types`} value={doc.fileTypes.join(", ")} dir="ltr" placeholder="pdf, jpg"
                      onChange={(e) => set({ ...doc, fileTypes: e.target.value.split(",").map((x) => x.trim().toLowerCase()).filter(Boolean) })} className={fieldInput} />
                  </div>
                  <div className="space-y-1">
                    <label htmlFor={`${id}-doc-${i}-size`} className="block text-sm font-semibold">{t("maxSizeMb")}</label>
                    <input id={`${id}-doc-${i}-size`} type="number" min={1} max={1024} value={doc.maxSizeMb ?? ""}
                      onChange={(e) => set({ ...doc, maxSizeMb: e.target.value ? Number(e.target.value) : null })} className={fieldInput} />
                  </div>
                </div>
                <TextPair id={`${id}-doc-${i}-name`} label={t("documentName")} value={doc.name} max={500} onChange={(name) => set({ ...doc, name })} />
                <TextPair id={`${id}-doc-${i}-description`} label={t("documentDescription")} value={doc.description} max={500} onChange={(description) => set({ ...doc, description })} />
                <label className="flex items-center gap-2">
                  <input type="checkbox" className="size-6" checked={doc.required} onChange={(e) => set({ ...doc, required: e.target.checked })} />
                  {t("required")}
                </label>
                <FilePicker id={`${id}-doc-${i}-template`} label={t("documentTemplate")} serviceId={detail.id} files={detail.files} value={doc.templateFileId}
                  csrfToken={csrfToken} onChange={(templateFileId) => set({ ...doc, templateFileId })} onUploaded={(next) => setDetail((d) => ({ ...d, files: next.files }))} />
              </div>
            )} />
        </Section>

        <Section title={t("steps")}>
          <ListEditor items={card.steps} onChange={(steps) => update({ steps })} addLabel={t("addStep")} ordered
            blank={() => ({ name: emptyText(), description: emptyText(), stage: "InProgress" as const, taskKey: null })}
            render={(step, i, set) => (
              <div className="space-y-2">
                <TextPair id={`${id}-step-${i}-name`} label={t("stepName")} value={step.name} max={500} onChange={(name) => set({ ...step, name })} />
                <TextPair id={`${id}-step-${i}-description`} label={t("stepDescription")} value={step.description} max={500} onChange={(description) => set({ ...step, description })} />
                <Select id={`${id}-step-${i}-stage`} label={t("stage")} value={step.stage} onChange={(v) => set({ ...step, stage: v as typeof step.stage })}
                  options={stepStages.map((s) => ({ value: s, label: t(`stages.${s}`) }))} />
                {card.fulfilment.tasks.length > 0 && (
                  <Select id={`${id}-step-${i}-task`} label={t("stepTask")} value={step.taskKey ?? ""} none={t("stepTaskNone")}
                    onChange={(v) => set({ ...step, taskKey: v || null })}
                    options={card.fulfilment.tasks.filter((x) => x.key).map((x) => ({ value: x.key, label: textOf(x.name, locale) || x.key }))} />
                )}
              </div>
            )} />
        </Section>

        <Section title={t("feeAndDelivery")}>
          <label className="flex items-center gap-2">
            <input type="checkbox" className="size-6" checked={card.fee.paid} onChange={(e) => update({ fee: { ...card.fee, paid: e.target.checked } })} />
            {t("paid")}
          </label>
          <div className="grid gap-3 sm:grid-cols-2">
            {card.fee.paid && (
              <div className="space-y-1">
                <label htmlFor={`${id}-amount`} className="block text-sm font-semibold">{t("amountAed")}</label>
                <input id={`${id}-amount`} type="number" min={0} step="0.01" dir="ltr" value={card.fee.amountAed ?? ""}
                  onChange={(e) => update({ fee: { ...card.fee, amountAed: e.target.value ? Number(e.target.value) : null } })} className={fieldInput} />
              </div>
            )}
            <div className="space-y-1">
              <label htmlFor={`${id}-days`} className="block text-sm font-semibold">{t("days")}</label>
              <input id={`${id}-days`} type="number" min={1} max={3650} value={card.days ?? ""} aria-describedby={`${id}-days-hint`}
                onChange={(e) => update({ days: e.target.value ? Number(e.target.value) : null })} className={fieldInput} />
              <p id={`${id}-days-hint`} className="text-sm text-ink-muted">{t("daysHint")}</p>
            </div>
          </div>
          <TextPair id={`${id}-fee-notes`} label={t("feeNotes")} value={card.fee.notes} max={500} onChange={(notes) => update({ fee: { ...card.fee, notes } })} />
          <CheckList legend={t("channels")} items={deliveryChannels.map((c) => ({ value: c, label: t(`channelNames.${c}`) }))} selected={card.channels}
            onChange={(channels) => update({ channels: channels as ServiceCard["channels"] })} />
          <CheckList legend={t("audiences")} items={targetAudiences.map((a) => ({ value: a, label: t(`audienceNames.${a}`) }))} selected={card.audiences}
            onChange={(audiences) => update({ audiences: audiences as ServiceCard["audiences"] })} />
        </Section>

        <Section title={t("partners")}>
          <ListEditor items={card.partners} onChange={(partners) => update({ partners })} addLabel={t("addPartner")}
            blank={() => ({ kind: "Entity" as const, departmentId: null, name: emptyText() })}
            render={(partner, i, set) => (
              <div className="space-y-2">
                <Select id={`${id}-partner-${i}-kind`} label={t("partnerKind")} value={partner.kind} onChange={(v) => set({ ...partner, kind: v as typeof partner.kind })}
                  options={partnerKinds.map((k) => ({ value: k, label: t(`partnerKinds.${k}`) }))} />
                {partner.kind === "Department" ? (
                  <Select id={`${id}-partner-${i}-department`} label={t("department")} value={partner.departmentId ?? ""} onChange={(v) => set({ ...partner, departmentId: v || null })}
                    options={choices.departments.map((d) => ({ value: d.id, label: textOf(d.name, locale) }))} none={t("none")} />
                ) : (
                  <TextPair id={`${id}-partner-${i}-name`} label={t("partnerName")} value={partner.name} max={500} onChange={(name) => set({ ...partner, name })} />
                )}
              </div>
            )} />
        </Section>

        <Section title={t("links")}>
          <CheckList legend={t("related")} items={choices.services.filter((s) => s.id !== detail.id).map((s) => ({ value: s.id, label: `${s.code} · ${textOf(s.name, locale)}` }))}
            selected={card.relatedServiceIds} onChange={(relatedServiceIds) => update({ relatedServiceIds })} />
          <CheckList legend={t("prerequisites")} items={choices.services.filter((s) => s.id !== detail.id).map((s) => ({ value: s.id, label: `${s.code} · ${textOf(s.name, locale)}` }))}
            selected={card.prerequisiteServiceIds} onChange={(prerequisiteServiceIds) => update({ prerequisiteServiceIds })} />
          <div className="space-y-1">
            <label htmlFor={`${id}-keywords`} className="block text-sm font-semibold">{t("keywords")}</label>
            <input id={`${id}-keywords`} value={card.keywords.join("; ")} aria-describedby={`${id}-keywords-hint`} dir="auto"
              onChange={(e) => update({ keywords: e.target.value.split(";").map((k) => k.trimStart()) })} className={fieldInput} />
            <p id={`${id}-keywords-hint`} className="text-sm text-ink-muted">{t("keywordsHint")}</p>
          </div>
        </Section>

        <Section title={t("faq")}>
          <ListEditor items={card.faq} onChange={(faq) => update({ faq })} addLabel={t("addFaq")} ordered
            blank={() => ({ question: emptyText(), answer: emptyText() })}
            render={(entry, i, set) => (
              <div className="space-y-2">
                <TextPair id={`${id}-faq-${i}-q`} label={t("question")} value={entry.question} max={500} onChange={(question) => set({ ...entry, question })} />
                <TextPair id={`${id}-faq-${i}-a`} label={t("answer")} value={entry.answer} max={20000} multiline onChange={(answer) => set({ ...entry, answer })} />
              </div>
            )} />
        </Section>

        {card.type === "Internal" && (
          <Section title={t("fulfilment")}>
            <FulfilmentEditor value={card.fulfilment} choices={choices} onChange={(fulfilment) => update({ fulfilment })} />
          </Section>
        )}

        {card.type === "Internal" && (
          <Section title={t("approvals")}>
            <p className="text-sm text-ink-muted">{t("approvalsHint")}</p>
            <ApprovalLevelsEditor legend={t("approvalsBefore")} hint={t("approvalsBeforeHint")} levels={card.approvals.beforeAssignment} choices={choices}
              onChange={(beforeAssignment) => update({ approvals: { ...card.approvals, beforeAssignment } })} />
            <ApprovalLevelsEditor legend={t("approvalsAfter")} hint={t("approvalsAfterHint")} levels={card.approvals.afterFulfilment} choices={choices}
              onChange={(afterFulfilment) => update({ approvals: { ...card.approvals, afterFulfilment } })} />
          </Section>
        )}

        <Section title={t("access")}>
          <fieldset className="space-y-1">
            <legend className="font-semibold">{t("accessMode")}</legend>
            {accessModes.map((mode) => (
              <label key={mode} className="flex items-center gap-2">
                <input type="radio" className="size-6" name={`${id}-access`} checked={card.access.mode === mode} onChange={() => update({ access: { ...card.access, mode } })} />
                {t(`accessModes.${mode}`)}
              </label>
            ))}
          </fieldset>
          {card.access.mode === "Restricted" && (
            <div className="space-y-3">
              <CheckList legend={t("accessGroups")} items={choices.groups.map((g) => ({ value: g.id, label: textOf(g.name, locale) }))} selected={card.access.groupIds}
                onChange={(groupIds) => update({ access: { ...card.access, groupIds } })} />
              <CheckList legend={t("accessRoles")} items={choices.roles.map((r) => ({ value: r.key, label: textOf(r.name, locale) }))} selected={card.access.roleKeys}
                onChange={(roleKeys) => update({ access: { ...card.access, roleKeys } })} />
              <CheckList legend={t("accessDepartments")} items={choices.departments.map((d) => ({ value: d.id, label: textOf(d.name, locale) }))} selected={card.access.departmentIds}
                onChange={(departmentIds) => update({ access: { ...card.access, departmentIds } })} />
              <CheckList legend={t("accessLocations")} items={choices.locations.map((l) => ({ value: l.id, label: textOf(l.path, locale) || textOf(l.name, locale) }))} selected={card.access.locationIds}
                onChange={(locationIds) => update({ access: { ...card.access, locationIds } })} />
              <PeopleList legend={t("accessUsers")} ids={card.access.userIds} onChange={(userIds) => update({ access: { ...card.access, userIds } })} />
            </div>
          )}
          <CheckList legend={t("onBehalf")} hint={t("onBehalfHint")} items={choices.roles.map((r) => ({ value: r.key, label: textOf(r.name, locale) }))}
            selected={card.onBehalfRoleKeys} onChange={(onBehalfRoleKeys) => update({ onBehalfRoleKeys })} />
        </Section>

        {editable && (
          <div className="space-y-1">
            <label htmlFor={`${id}-note`} className="block text-sm font-semibold">{t("changeNote")}</label>
            <input id={`${id}-note`} value={changeNote} maxLength={2000} dir="auto" onChange={(e) => setChangeNote(e.target.value)} className={fieldInput} />
          </div>
        )}
      </fieldset>

      <section aria-labelledby={`${id}-versions`} className="space-y-2">
        <h3 id={`${id}-versions`} className="text-lg font-semibold">{t("versions")}</h3>
        {versions === null ? (
          <button type="button" className={smallButton} onClick={() => void getJson<ServiceVersion[]>(`${base}/versions`).then(setVersions).catch(() => setVersions([]))}>{t("showVersions")}</button>
        ) : (
          <ol className="space-y-1">
            {versions.map((v) => (
              <li key={v.id}>
                {t("versionLine", { number: v.number, state: t(`versionStates.${v.state}`) })}
                {v.changeNote && <span className="ms-2 text-ink-muted" dir="auto">{v.changeNote}</span>}
              </li>
            ))}
          </ol>
        )}
      </section>
    </div>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="space-y-3 rounded-lg border border-line p-4">
      <h3 id={id} className="text-lg font-semibold">{title}</h3>
      {children}
    </section>
  );
}

/** An English and an Arabic input (or text area), each in its own direction. */
export function TextPair({ id, label, hint, value, max, multiline, onChange }: {
  id: string; label: string; hint?: string; value: Text; max: number; multiline?: boolean; onChange: (value: Text) => void;
}) {
  const t = useTranslations("directory");
  const field = (lang: "en" | "ar") => {
    const props = {
      id: `${id}-${lang}`, lang, dir: lang === "ar" ? ("rtl" as const) : ("ltr" as const), maxLength: max, value: value[lang] ?? "",
      "aria-describedby": hint ? `${id}-hint` : undefined, className: fieldInput,
    };
    return multiline
      ? <textarea {...props} rows={5} onChange={(e) => onChange({ ...value, [lang]: e.target.value })} />
      : <input {...props} onChange={(e) => onChange({ ...value, [lang]: e.target.value })} />;
  };
  return (
    <fieldset className="grid gap-2 sm:grid-cols-2">
      <legend className="font-semibold">{label}</legend>
      <div className="space-y-1">
        <label htmlFor={`${id}-en`} className="block text-sm">{t("inEnglish")}</label>
        {field("en")}
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-ar`} className="block text-sm">{t("inArabic")}</label>
        {field("ar")}
      </div>
      {hint && <p id={`${id}-hint`} className="text-sm text-ink-muted sm:col-span-2">{hint}</p>}
    </fieldset>
  );
}

function Select({ id, label, value, options, none, onChange }: {
  id: string; label: string; value: string; options: { value: string; label: string }[]; none?: string; onChange: (value: string) => void;
}) {
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-sm font-semibold">{label}</label>
      <select id={id} value={value} onChange={(e) => onChange(e.target.value)} className={fieldInput}>
        {none !== undefined && <option value="">{none}</option>}
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </div>
  );
}

function CheckList({ legend, hint, items, selected, onChange }: {
  legend: string; hint?: string; items: { value: string; label: string }[]; selected: string[]; onChange: (selected: string[]) => void;
}) {
  const t = useTranslations("catalog.editor");
  const id = useId();
  return (
    <fieldset className="space-y-1" aria-describedby={hint ? `${id}-hint` : undefined}>
      <legend className="font-semibold">{legend}</legend>
      {hint && <p id={`${id}-hint`} className="text-sm text-ink-muted">{hint}</p>}
      {items.length === 0 ? <p className="text-sm text-ink-muted">{t("nothingToChoose")}</p> : (
        <div className="grid gap-1 sm:grid-cols-2">
          {items.map((item) => (
            <label key={item.value} className="flex items-center gap-2">
              <input type="checkbox" className="size-6" checked={selected.includes(item.value)}
                onChange={(e) => onChange(e.target.checked ? [...selected, item.value] : selected.filter((v) => v !== item.value))} />
              <span dir="auto">{item.label}</span>
            </label>
          ))}
        </div>
      )}
    </fieldset>
  );
}

/** An ordered list of entries with add, remove and move buttons (keyboard and pointer alike). */
function ListEditor<T>({ items, onChange, render, blank, addLabel, ordered }: {
  items: T[]; onChange: (items: T[]) => void; render: (item: T, index: number, set: (item: T) => void) => ReactNode; blank: () => T; addLabel: string; ordered?: boolean;
}) {
  const t = useTranslations("catalog.editor");
  const move = (from: number, to: number) => {
    const next = [...items];
    const [item] = next.splice(from, 1);
    next.splice(to, 0, item!);
    onChange(next);
  };
  return (
    <div className="space-y-3">
      <ol className="space-y-3">
        {items.map((item, i) => (
          <li key={i} className="space-y-2 rounded-md border border-line p-3">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-semibold">{t("entry", { number: i + 1 })}</span>
              {ordered && <button type="button" className={smallButton} disabled={i === 0} onClick={() => move(i, i - 1)} aria-label={t("moveUp", { number: i + 1 })}>{t("up")}</button>}
              {ordered && <button type="button" className={smallButton} disabled={i === items.length - 1} onClick={() => move(i, i + 1)} aria-label={t("moveDown", { number: i + 1 })}>{t("down")}</button>}
              <button type="button" className={smallButton} onClick={() => onChange(items.filter((_, j) => j !== i))} aria-label={t("removeEntry", { number: i + 1 })}>{t("remove")}</button>
            </div>
            {render(item, i, (next) => onChange(items.map((x, j) => (j === i ? next : x))))}
          </li>
        ))}
      </ol>
      <button type="button" className={smallButton} onClick={() => onChange([...items, blank()])}>{addLabel}</button>
    </div>
  );
}

function OwnerPicker({ id, ownerId, onChange }: { id: string; ownerId: string | null; onChange: (id: string | null) => void }) {
  const t = useTranslations("catalog.editor");
  return (
    <div className="space-y-1">
      <PeopleList legend={t("owner")} ids={ownerId ? [ownerId] : []} single onChange={(ids) => onChange(ids[0] ?? null)} idPrefix={id} />
    </div>
  );
}

/** People chosen by name (the forms people search, names only). */

function FilePicker({ id, label, serviceId, files, value, csrfToken, onChange, onUploaded, accept }: {
  id: string; label: string; serviceId: string; files: ServiceDetail["files"]; value: string | null; csrfToken: string; accept?: string;
  onChange: (id: string | null) => void; onUploaded: (detail: ServiceDetail) => void;
}) {
  const t = useTranslations("catalog.editor");
  const { status, run, busy } = useSaver();
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-sm font-semibold">{label}</label>
      <select id={id} value={value ?? ""} onChange={(e) => onChange(e.target.value || null)} className={fieldInput}>
        <option value="">{t("none")}</option>
        {files.map((f) => <option key={f.id} value={f.id}>{f.fileName}</option>)}
      </select>
      <label className="block text-sm">
        {t("uploadFile")}
        <input type="file" accept={accept} disabled={busy} className="mt-1 block text-sm"
          onChange={(e) => {
            const file = e.target.files?.[0];
            e.target.value = "";
            if (!file) return;
            void run(async () => {
              const uploaded = await postFile<{ id: string }>(`/v1/catalog/services/${serviceId}/files`, file, csrfToken);
              onUploaded(await getJson<ServiceDetail>(`/v1/catalog/services/${serviceId}`));
              onChange(uploaded.id);
            });
          }} />
      </label>
      <CatalogStatusLine status={status} />
    </div>
  );
}

function ImagePicker({ serviceId, files, imageFileId, csrfToken, onChange, onUploaded }: {
  serviceId: string; files: ServiceDetail["files"]; imageFileId: string | null; csrfToken: string; onChange: (id: string | null) => void; onUploaded: (detail: ServiceDetail) => void;
}) {
  const t = useTranslations("catalog.editor");
  const id = useId();
  return (
    <FilePicker id={id} label={t("image")} serviceId={serviceId} files={files.filter((f) => f.contentType.startsWith("image/"))} value={imageFileId}
      csrfToken={csrfToken} accept="image/png,image/jpeg,image/gif,image/webp" onChange={onChange} onUploaded={onUploaded} />
  );
}

/** The input a lifecycle action needs: a comment, a bilingual message or name, or a new code. */
function ActionPanel({ kind, busy, onSubmit, onCancel }: {
  kind: "sendBack" | "suspend" | "copy" | "template"; busy: boolean; onSubmit: (value: { text: string; en: string; ar: string; code: string }) => Promise<void>; onCancel: () => void;
}) {
  const t = useTranslations("catalog.editor");
  const id = useId();
  const [text, setText] = useState("");
  const [pair, setPair] = useState<Text>(emptyText());
  const [code, setCode] = useState("");
  return (
    <form className="space-y-3 rounded-lg border border-line p-3" aria-label={t(`panels.${kind}`)}
      onSubmit={(e) => {
        e.preventDefault();
        void onSubmit({ text: text.trim(), en: pair.en?.trim() ?? "", ar: pair.ar?.trim() ?? "", code: code.trim() });
      }}>
      <h3 className="font-semibold">{t(`panels.${kind}`)}</h3>
      {kind === "sendBack" && (
        <div className="space-y-1">
          <label htmlFor={`${id}-comment`} className="block text-sm font-semibold">{t("comment")}</label>
          <textarea id={`${id}-comment`} value={text} onChange={(e) => setText(e.target.value)} required maxLength={2000} rows={3} dir="auto" className={fieldInput} />
        </div>
      )}
      {kind === "copy" && (
        <div className="space-y-1">
          <label htmlFor={`${id}-code`} className="block text-sm font-semibold">{t("code")}</label>
          <input id={`${id}-code`} value={code} onChange={(e) => setCode(e.target.value)} required maxLength={30} dir="ltr" className={`${fieldInput} font-mono uppercase`} />
        </div>
      )}
      {kind !== "sendBack" && (
        <TextPair id={`${id}-text`} label={kind === "suspend" ? t("suspendMessage") : t("name")} value={pair} max={kind === "suspend" ? 1000 : 200}
          multiline={kind === "suspend"} onChange={setPair} />
      )}
      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryAction} disabled={busy}>{t(`panels.${kind}Confirm`)}</button>
        <button type="button" className={smallButton} onClick={onCancel}>{t("cancel")}</button>
      </div>
    </form>
  );
}

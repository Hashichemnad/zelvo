"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, primaryButton, secondaryButton } from "@/components/directory/common";
import { FormRenderer } from "@/components/forms/FormRenderer";
import { fieldInput } from "@/components/forms/ui";
import { useRouter } from "@/i18n/navigation";
import { ApiError, patchJson, postJson } from "@/lib/client-api";
import { ciStatuses, classesByFamily, classFields, textIn, type CiClass, type CiStatus, type ConfigurationItem } from "@/lib/cmdb";
import { selectableNodes, type GroupSummary, type TreeNode } from "@/lib/directory";
import { checkAnswers } from "@/lib/forms/check";
import type { AnswerError, Answers, FormReferences } from "@/lib/forms/types";
import type { Locale } from "@/lib/incidents";
import { CmdbStatusLine, FamilyLabel, useSaver } from "./common";

const empty = "00000000-0000-0000-0000-000000000000";

/** Attribute values and the names of the users and locations they refer to, for the form renderer. */
function answersOf(item: ConfigurationItem | null): { answers: Answers; references: FormReferences } {
  const answers: Answers = {};
  const references: FormReferences = { users: {}, locations: {}, files: {} };
  for (const a of item?.attributes ?? []) {
    if (a.value === null || a.value === undefined) continue;
    answers[a.field.key] = a.value;
    if (a.field.type === "User" && typeof a.value === "string" && a.display) references.users[a.value] = a.display.en ?? a.display.ar;
    if (a.field.type === "Location" && typeof a.value === "string" && a.display) references.locations[a.value] = a.display;
  }
  return { answers, references };
}

/**
 * Adds or changes a configuration item (task G1): class, name, description, status, owner group,
 * location and the class's attributes, filled with the same renderer and checks as forms (C1).
 */
export function CiEditor({
  item,
  classes,
  groups,
  locations,
  csrfToken,
  onSaved,
  onCancel,
}: {
  /** The item to change; null adds one. */
  item: ConfigurationItem | null;
  classes: CiClass[];
  groups: GroupSummary[] | null;
  locations: TreeNode[];
  csrfToken: string;
  onSaved?: (item: ConfigurationItem) => void;
  onCancel?: () => void;
}) {
  const t = useTranslations("cmdb.editor");
  const tStatuses = useTranslations("cmdb.statuses");
  const tFamilies = useTranslations("cmdb.families");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const initial = answersOf(item);
  const usable = classes.filter((c) => c.selectable || c.id === item?.class.id);
  const [classId, setClassId] = useState(item?.class.id ?? "");
  const [name, setName] = useState(item?.name ?? "");
  const [description, setDescription] = useState({ en: item?.description.en ?? "", ar: item?.description.ar ?? "" });
  const [status, setStatus] = useState<CiStatus>(item?.status ?? "Active");
  const [groupId, setGroupId] = useState(item?.ownerGroup?.id ?? "");
  const [locationId, setLocationId] = useState(item?.location?.id ?? "");
  const [answers, setAnswers] = useState<Answers>(initial.answers);
  const [errors, setErrors] = useState<AnswerError[]>([]);
  const [fieldErrors, setFieldErrors] = useState<string[]>([]);
  const { status: saveStatus, run, busy } = useSaver();
  const ciClass = classes.find((c) => c.id === classId);
  const fields = classFields(ciClass);
  const defaults: Answers = Object.fromEntries(fields.filter((f) => f.defaultValue !== undefined && f.defaultValue !== null).map((f) => [f.key, f.defaultValue!]));

  async function save() {
    const checked = checkAnswers(fields, answers, item ? {} : defaults);
    setErrors(checked.errors);
    setFieldErrors([]);
    if (checked.errors.length > 0 || !classId || !name.trim()) return;
    const body = {
      classId,
      name: name.trim(),
      description: { en: description.en.trim() || null, ar: description.ar.trim() || null },
      status,
      ownerGroupId: groupId || (item ? empty : null),
      locationId: locationId || (item ? empty : null),
      attributes: checked.toSend,
    };
    const saved = await run(async () => {
      try {
        return item
          ? await patchJson<ConfigurationItem>(`/v1/cmdb/items/${item.id}`, body, csrfToken, { "If-Match": item.etag })
          : await postJson<ConfigurationItem>("/v1/cmdb/items", body, csrfToken);
      } catch (error) {
        if (error instanceof ApiError) {
          if (Array.isArray(error.details.attributeErrors)) {
            setErrors((error.details.attributeErrors as AnswerError[]).map((e) => ({ ...e, field: e.field.replace(/^attributes\./, "") })));
          }
          if (error.details.errors && typeof error.details.errors === "object") setFieldErrors(Object.keys(error.details.errors));
        }
        throw error;
      }
    });
    if (!saved) return;
    if (onSaved) onSaved(saved);
    else router.push(`/cmdb/${saved.id}`);
  }

  const nodes = selectableNodes(locations);
  return (
    <form className="space-y-5" aria-labelledby={`${id}-heading`} noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}>
      <h2 id={`${id}-heading`} className="text-xl font-semibold">{item ? t("editHeading", { number: item.number }) : t("newHeading")}</h2>
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-class`} className="block font-semibold">{t("class")} <span className="text-bad">({t("required")})</span></label>
          <select id={`${id}-class`} value={classId} required aria-describedby={item ? `${id}-class-hint` : undefined}
            aria-invalid={fieldErrors.includes("classId") || undefined} onChange={(e) => setClassId(e.target.value)} className={fieldInput}>
            <option value="">{t("chooseClass")}</option>
            {classesByFamily(usable).map(({ family, classes: members }) => (
              <optgroup key={family} label={tFamilies(family)}>
                {members.map((c) => (
                  <option key={c.id} value={c.id}>{textIn(c.path, locale) || textIn(c.name, locale) || c.key}</option>
                ))}
              </optgroup>
            ))}
          </select>
          {item && <p id={`${id}-class-hint`} className="text-sm text-ink-muted">{t("classHint")}</p>}
          {ciClass && <p className="text-sm text-ink-muted"><FamilyLabel family={ciClass.family} /></p>}
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-name`} className="block font-semibold">{t("name")} <span className="text-bad">({t("required")})</span></label>
          <input id={`${id}-name`} dir="auto" value={name} maxLength={200} required aria-invalid={fieldErrors.includes("name") || undefined}
            onChange={(e) => setName(e.target.value)} className={fieldInput} />
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-status`} className="block font-semibold">{t("status")}</label>
          <select id={`${id}-status`} value={status} onChange={(e) => setStatus(e.target.value as CiStatus)} className={fieldInput}>
            {ciStatuses.map((s) => <option key={s} value={s}>{tStatuses(s)}</option>)}
          </select>
        </div>
        {groups && (
          <div className="space-y-1">
            <label htmlFor={`${id}-group`} className="block font-semibold">{t("ownerGroup")}</label>
            <select id={`${id}-group`} value={groupId} aria-invalid={fieldErrors.includes("ownerGroupId") || undefined} onChange={(e) => setGroupId(e.target.value)} className={fieldInput}>
              <option value="">{t("noGroup")}</option>
              {groups.filter((g) => g.active || g.id === groupId).map((g) => <option key={g.id} value={g.id}>{textIn(g.name, locale)}</option>)}
            </select>
          </div>
        )}
        <div className="space-y-1">
          <label htmlFor={`${id}-location`} className="block font-semibold">{t("location")}</label>
          <select id={`${id}-location`} value={locationId} aria-invalid={fieldErrors.includes("locationId") || undefined} onChange={(e) => setLocationId(e.target.value)} className={fieldInput}>
            <option value="">{t("noLocation")}</option>
            {item?.location && !nodes.some((n) => n.id === item.location!.id) && <option value={item.location.id}>{textIn(item.location.path, locale)}</option>}
            {nodes.map((n) => <option key={n.id} value={n.id}>{textIn(n.path, locale) || textIn(n.name, locale)}</option>)}
          </select>
        </div>
      </div>
      <BilingualInputs id={`${id}-description`} label={t("description")} maxLength={4000} en={description.en} ar={description.ar} onChange={setDescription} />
      <fieldset className="space-y-3 rounded-lg border border-line p-4">
        <legend className="px-1 font-semibold">{t("attributes")}</legend>
        {!ciClass ? (
          <p className="text-sm text-ink-muted">{t("chooseClassFirst")}</p>
        ) : fields.length === 0 ? (
          <p className="text-sm text-ink-muted">{t("noAttributes")}</p>
        ) : (
          <FormRenderer fields={fields.map((field) => ({ field }))} answers={answers} onChange={setAnswers} defaults={item ? {} : defaults}
            references={initial.references} errors={errors} contentLocale={locale} locations={locations} csrfToken={csrfToken} idPrefix={`${id}-attr`} />
        )}
      </fieldset>
      {errors.length > 0 && <p role="alert" className="font-semibold text-bad">{t("checkAttributes")}</p>}
      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryButton} disabled={busy || !classId || !name.trim()}>{item ? t("save") : t("create")}</button>
        {onCancel && <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>}
      </div>
      <CmdbStatusLine status={saveStatus} />
    </form>
  );
}

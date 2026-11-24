"use client";

import { useId, useMemo, useRef, useState } from "react";
import { NextIntlClientProvider, useLocale, useTranslations, type AbstractIntlMessages } from "next-intl";
import {
  DndContext,
  KeyboardSensor,
  PointerSensor,
  closestCenter,
  useDraggable,
  useSensor,
  useSensors,
  type Announcements,
  type DragEndEvent,
} from "@dnd-kit/core";
import { SortableContext, arrayMove, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { ApiError, postJson, putJson } from "@/lib/client-api";
import type { TreeNode } from "@/lib/directory";
import { expand, fieldTypes, textIn, type Answers, type FieldSetDetail, type FieldType, type FormDetail, type FormElement, type FormField, type FormProblem, type LocalizedName } from "@/lib/forms/types";
import { checkAnswers } from "@/lib/forms/check";
import { FieldProperties } from "./FieldProperties";
import { FormRenderer } from "./FormRenderer";
import { fieldButton, primaryAction } from "./ui";

type Locale = "en" | "ar";

/** A published field set a form can use. */
export interface AvailableFieldSet {
  id: string;
  name: LocalizedName;
  fields: FormField[];
}

export interface FormBuilderProps {
  /** A form (elements and field sets) or a field set (fields only). */
  kind: "form" | "fieldSet";
  initial: FormDetail | FieldSetDetail;
  csrfToken: string;
  /** Published field sets (forms only). */
  fieldSets: AvailableFieldSet[];
  /** Locations for the preview's location fields. */
  locations: TreeNode[];
  /** The renderer's texts in both languages, for the preview in either language. */
  previewMessages: Record<Locale, AbstractIntlMessages>;
}

interface Item {
  uid: string;
  element: FormElement;
}

let counter = 0;
/** Ids of elements added in the browser (never rendered on the server). */
const uid = () => `el-new-${(counter++).toString(36)}`;

/** Ids of the elements loaded with the page: stable, so the server and the browser render the same. */
function toItems(kind: FormBuilderProps["kind"], detail: FormDetail | FieldSetDetail, prefix = "el"): Item[] {
  const elements: FormElement[] =
    kind === "form" ? ((detail as FormDetail).draft?.elements ?? []) : ((detail as FieldSetDetail).draft?.fields ?? []).map((field) => ({ type: "Field", field }));
  return elements.map((element, index) => ({ uid: `${prefix}-${index}`, element }));
}

/** A key not used yet, from the type: text1, text2, … */
function newKey(type: FieldType, taken: Set<string>): string {
  const base = type.charAt(0).toLowerCase() + type.slice(1);
  let n = 1;
  while (taken.has(`${base}${n}`)) n++;
  return `${base}${n}`;
}

function PaletteItem({ type, label, onAdd }: { type: FieldType; label: string; onAdd: () => void }) {
  const t = useTranslations("forms.builder");
  const { attributes, listeners, setNodeRef, transform, isDragging } = useDraggable({ id: `palette:${type}`, data: { type } });
  return (
    <li className="flex items-center gap-1">
      <button type="button" ref={setNodeRef} {...listeners} {...attributes} aria-label={t("dragNew", { type: label })}
        style={{ transform: CSS.Translate.toString(transform), opacity: isDragging ? 0.6 : 1 }}
        className="cursor-grab rounded border border-line px-1.5 text-ink-muted" aria-roledescription={t("draggable")}>
        ⋮⋮
      </button>
      <button type="button" className={`${fieldButton} flex-1 text-start`} onClick={onAdd}>
        {t("add", { type: label })}
      </button>
    </li>
  );
}

function CanvasItem({
  item,
  index,
  count,
  selected,
  title,
  subtitle,
  badges,
  onSelect,
  onMove,
  onDuplicate,
  onRemove,
}: {
  item: Item;
  index: number;
  count: number;
  selected: boolean;
  title: string;
  subtitle: string;
  badges: string[];
  onSelect: () => void;
  onMove: (by: number) => void;
  onDuplicate?: () => void;
  onRemove: () => void;
}) {
  const t = useTranslations("forms.builder");
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: item.uid });
  return (
    <li ref={setNodeRef} style={{ transform: CSS.Transform.toString(transform), transition, opacity: isDragging ? 0.7 : 1 }}
      className={`rounded-lg border p-3 ${selected ? "border-brand bg-surface-muted" : "border-line bg-surface"}`} data-testid="canvas-item">
      <div className="flex items-start gap-2">
        <button type="button" {...attributes} {...listeners} aria-label={t("dragMove", { name: title })} aria-roledescription={t("sortable")}
          className="cursor-grab rounded border border-line px-1.5 text-ink-muted">
          ⋮⋮
        </button>
        <button type="button" onClick={onSelect} aria-pressed={selected} className="flex-1 text-start focus-visible:outline-2 focus-visible:outline-brand">
          <span className="block font-semibold">{title}</span>
          <span className="block text-sm text-ink-muted">{subtitle}</span>
          {badges.length > 0 && <span className="mt-1 flex flex-wrap gap-1">{badges.map((b) => <span key={b} className="rounded bg-surface-muted px-1.5 text-xs">{b}</span>)}</span>}
        </button>
      </div>
      <div className="mt-2 flex flex-wrap gap-1">
        <button type="button" className={fieldButton} disabled={index === 0} onClick={() => onMove(-1)}>{t("moveUp", { name: title })}</button>
        <button type="button" className={fieldButton} disabled={index === count - 1} onClick={() => onMove(1)}>{t("moveDown", { name: title })}</button>
        {onDuplicate && <button type="button" className={fieldButton} onClick={onDuplicate}>{t("duplicate", { name: title })}</button>}
        <button type="button" className={fieldButton} onClick={onRemove}>{t("remove", { name: title })}</button>
      </div>
    </li>
  );
}

/**
 * The drag-and-drop form builder (module 2; dnd-kit, product owner decision 2026-11-24): a palette
 * of field types (and field sets), the form's fields in order, and the selected field's properties
 * with its rules. Everything also works from the keyboard: add buttons, move up and down, and
 * keyboard dragging with announcements in the page's language. A live preview shows the form on
 * desktop and mobile, in English and Arabic, before it is published.
 */
export function FormBuilder({ kind, initial, csrfToken, fieldSets, locations, previewMessages }: FormBuilderProps) {
  const t = useTranslations("forms.builder");
  const tTypes = useTranslations("forms.types");
  const tProblems = useTranslations("forms.problems");
  const locale = useLocale() as Locale;
  const id = useId();
  const [detail, setDetail] = useState(initial);
  const [items, setItems] = useState<Item[]>(() => toItems(kind, initial));
  const [selected, setSelected] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const [status, setStatus] = useState<{ kind: "idle" | "saving" | "saved" | "published" | "failed"; code?: string | null }>({ kind: "idle" });
  const [problems, setProblems] = useState<FormProblem[]>(initial.problems);
  const [view, setView] = useState<"build" | "preview">("build");
  const [device, setDevice] = useState<"desktop" | "mobile">("desktop");
  const [previewLocale, setPreviewLocale] = useState<Locale>(locale);
  const [previewAnswers, setPreviewAnswers] = useState<Answers>({});
  const [previewChecked, setPreviewChecked] = useState(false);
  const announce = useRef<HTMLParagraphElement>(null);
  const base = kind === "form" ? "/v1/forms" : "/v1/field-sets";
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 5 } }), useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }));

  const setsById = useMemo(() => Object.fromEntries(fieldSets.map((s) => [s.id, s])), [fieldSets]);
  const elements = items.map((i) => i.element);
  const resolved = expand({ elements }, Object.fromEntries(fieldSets.map((s) => [s.id, s.fields])));
  const keys = new Set(resolved.map((r) => r.field.key));
  const selectedItem = items.find((i) => i.uid === selected) ?? null;

  const update = (next: Item[]) => {
    setItems(next);
    setDirty(true);
    setStatus({ kind: "idle" });
  };

  function addField(type: FieldType, at = items.length) {
    const field: FormField = { key: newKey(type, keys), type, label: { [locale]: tTypes(type) } };
    if (type === "Dropdown" || type === "Radio") field.options = [{ value: "option1", label: { [locale]: t("optionDefault", { number: 1 }) } }];
    if (type === "Table") field.columns = [{ key: "column1", type: "Text", label: { [locale]: t("columnDefault", { number: 1 }) } }];
    const item = { uid: uid(), element: { type: "Field" as const, field } };
    update([...items.slice(0, at), item, ...items.slice(at)]);
    setSelected(item.uid);
    if (announce.current) announce.current.textContent = t("added", { type: tTypes(type) });
  }

  function addFieldSet(setId: string) {
    const item = { uid: uid(), element: { type: "FieldSet" as const, fieldSetId: setId } };
    update([...items, item]);
    setSelected(item.uid);
  }

  const titleOf = (item: Item) =>
    item.element.type === "Field"
      ? textIn(item.element.field.label, locale) || item.element.field.key
      : textIn(setsById[item.element.fieldSetId]?.name, locale) || t("unknownFieldSet");

  function onDragEnd(event: DragEndEvent) {
    const { active, over } = event;
    const from = String(active.id);
    if (from.startsWith("palette:")) {
      const type = from.slice("palette:".length) as FieldType;
      const at = over ? items.findIndex((i) => i.uid === over.id) : -1;
      addField(type, at < 0 ? items.length : at + 1);
      return;
    }
    if (!over || over.id === active.id) return;
    const oldIndex = items.findIndex((i) => i.uid === from);
    const newIndex = items.findIndex((i) => i.uid === over.id);
    if (oldIndex >= 0 && newIndex >= 0) update(arrayMove(items, oldIndex, newIndex));
  }

  const announcements: Announcements = {
    onDragStart: ({ active }) => t("dndStart", { name: labelOfId(String(active.id)) }),
    onDragOver: ({ active, over }) => (over ? t("dndOver", { name: labelOfId(String(active.id)), position: positionOf(String(over.id)) }) : t("dndOutside", { name: labelOfId(String(active.id)) })),
    onDragEnd: ({ active, over }) => (over ? t("dndEnd", { name: labelOfId(String(active.id)), position: positionOf(String(over.id)) }) : t("dndCancel", { name: labelOfId(String(active.id)) })),
    onDragCancel: ({ active }) => t("dndCancel", { name: labelOfId(String(active.id)) }),
  };
  function labelOfId(value: string) {
    if (value.startsWith("palette:")) return tTypes(value.slice("palette:".length) as FieldType);
    const item = items.find((i) => i.uid === value);
    return item ? titleOf(item) : value;
  }
  function positionOf(value: string) {
    return String(items.findIndex((i) => i.uid === value) + 1);
  }

  const schema = () => (kind === "form" ? { elements } : { fields: elements.flatMap((e) => (e.type === "Field" ? [e.field] : [])) });

  async function save(): Promise<FormDetail | FieldSetDetail | null> {
    setStatus({ kind: "saving" });
    try {
      const saved = await putJson<FormDetail | FieldSetDetail>(`${base}/${detail.id}/draft`, { schema: schema() }, csrfToken, { "If-Match": `"${detail.etag}"` });
      setDetail(saved);
      setProblems(saved.problems);
      setDirty(false);
      setStatus({ kind: "saved" });
      return saved;
    } catch (error) {
      setStatus({ kind: "failed", code: error instanceof ApiError ? error.code : null });
      return null;
    }
  }

  async function publish() {
    const current = dirty ? await save() : detail;
    if (!current) return;
    setStatus({ kind: "saving" });
    try {
      const published = await postJson<FormDetail | FieldSetDetail>(`${base}/${current.id}/publish`, undefined, csrfToken, { "If-Match": `"${current.etag}"` });
      setDetail(published);
      setProblems(published.problems);
      setStatus({ kind: "published" });
    } catch (error) {
      if (error instanceof ApiError && Array.isArray(error.details.problems)) setProblems(error.details.problems as FormProblem[]);
      setStatus({ kind: "failed", code: error instanceof ApiError ? error.code : null });
    }
  }

  async function discard() {
    setStatus({ kind: "saving" });
    try {
      const reset = await postJson<FormDetail>(`${base}/${detail.id}/discard`, undefined, csrfToken);
      setDetail(reset);
      setItems(toItems(kind, reset, uid()));
      setProblems(reset.problems);
      setDirty(false);
      setStatus({ kind: "saved" });
    } catch (error) {
      setStatus({ kind: "failed", code: error instanceof ApiError ? error.code : null });
    }
  }

  const preview = checkAnswers(resolved.map((r) => r.field), previewAnswers, {});
  const statusText =
    status.kind === "saving" ? t("saving")
      : status.kind === "saved" ? t("saved")
        : status.kind === "published" ? t("published")
          : status.kind === "failed" ? (status.code && t.has(`errors.${status.code}`) ? t(`errors.${status.code}`) : t("failed"))
            : dirty ? t("unsaved") : "";

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2" role="toolbar" aria-label={t("toolbar")}>
        <button type="button" className={fieldButton} disabled={status.kind === "saving" || detail.state === "Retired"} onClick={() => void save()}>{t("saveDraft")}</button>
        <button type="button" className={primaryAction} disabled={status.kind === "saving" || detail.state === "Retired"} onClick={() => void publish()}>{t("publish")}</button>
        {detail.publishedVersion !== null && kind === "form" && (detail.hasUnpublishedChanges || dirty) && (
          <button type="button" className={fieldButton} disabled={status.kind === "saving"} onClick={() => void discard()}>{t("discard")}</button>
        )}
        <span className="ms-auto flex gap-1" role="group" aria-label={t("viewChoice")}>
          <button type="button" className={fieldButton} aria-pressed={view === "build"} onClick={() => setView("build")}>{t("viewBuild")}</button>
          <button type="button" className={fieldButton} aria-pressed={view === "preview"} onClick={() => setView("preview")}>{t("viewPreview")}</button>
        </span>
      </div>
      <p role="status" aria-live="polite" className={status.kind === "failed" ? "text-bad" : "text-ink-muted"}>{statusText}</p>
      <p className="text-sm">
        {t("stateLine", {
          state: t(`states.${detail.state}`),
          version: detail.publishedVersion ?? 0,
        })}
      </p>

      {problems.length > 0 && (
        <section aria-labelledby={`${id}-problems`} className="rounded-lg border border-bad p-3">
          <h3 id={`${id}-problems`} className="font-semibold text-bad">{t("problemsTitle", { count: problems.length })}</h3>
          <ul className="list-disc ps-6">
            {problems.map((p, i) => (
              <li key={i}>
                {p.field ? <span dir="ltr" className="font-mono">{p.field}</span> : null}
                {p.field ? ": " : ""}
                <span>{tProblems.has(p.code) ? tProblems(p.code) : p.message}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      {view === "build" ? (
        <DndContext id={`${id}-dnd`} sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}
          accessibility={{ announcements, screenReaderInstructions: { draggable: t("dndInstructions") } }}>
          <div className="grid gap-4 lg:grid-cols-[14rem_minmax(0,1fr)_minmax(0,24rem)]">
            <aside aria-labelledby={`${id}-palette`} className="space-y-3">
              <h3 id={`${id}-palette`} className="font-semibold">{t("palette")}</h3>
              <ul className="space-y-1">
                {fieldTypes.map((type) => <PaletteItem key={type} type={type} label={tTypes(type)} onAdd={() => addField(type)} />)}
              </ul>
              {kind === "form" && (
                <>
                  <h3 className="font-semibold">{t("fieldSets")}</h3>
                  {fieldSets.length === 0 ? (
                    <p className="text-sm text-ink-muted">{t("noFieldSets")}</p>
                  ) : (
                    <ul className="space-y-1">
                      {fieldSets.map((set) => {
                        const used = elements.some((e) => e.type === "FieldSet" && e.fieldSetId === set.id);
                        return (
                          <li key={set.id}>
                            <button type="button" className={`${fieldButton} w-full text-start`} disabled={used} onClick={() => addFieldSet(set.id)}>
                              {t("addFieldSet", { name: textIn(set.name, locale) })}
                            </button>
                          </li>
                        );
                      })}
                    </ul>
                  )}
                </>
              )}
              <p ref={announce} role="status" aria-live="polite" className="sr-only" />
            </aside>

            <section aria-labelledby={`${id}-canvas`} className="space-y-2">
              <h3 id={`${id}-canvas`} className="font-semibold">{kind === "form" ? t("canvasForm") : t("canvasFieldSet")}</h3>
              {items.length === 0 && <p className="rounded-lg border border-dashed border-line p-6 text-center text-ink-muted">{t("canvasEmpty")}</p>}
              <SortableContext items={items.map((i) => i.uid)} strategy={verticalListSortingStrategy}>
                <ol className="space-y-2">
                  {items.map((item, index) => {
                    const element = item.element;
                    const field = element.type === "Field" ? element.field : null;
                    const badges = field
                      ? [
                          field.required ? t("badgeRequired") : null,
                          field.readOnly ? t("badgeReadOnly") : null,
                          field.rules?.length ? t("badgeRules", { count: field.rules.length }) : null,
                          field.profileDefault ? t("badgeProfile") : null,
                          !field.label.en?.trim() || !field.label.ar?.trim() ? t("badgeMissingLanguage") : null,
                        ].filter((b): b is string => !!b)
                      : [t("badgeFields", { count: setsById[(element as { fieldSetId: string }).fieldSetId]?.fields.length ?? 0 })];
                    return (
                      <CanvasItem key={item.uid} item={item} index={index} count={items.length} selected={selected === item.uid}
                        title={titleOf(item)}
                        subtitle={field ? `${tTypes(field.type)} · ${field.key}` : t("fieldSetElement")}
                        badges={badges}
                        onSelect={() => setSelected(item.uid)}
                        onMove={(by) => update(arrayMove(items, index, index + by))}
                        onDuplicate={field ? () => {
                          const copy = { uid: uid(), element: { type: "Field" as const, field: { ...field, key: newKey(field.type, keys) } } };
                          update([...items.slice(0, index + 1), copy, ...items.slice(index + 1)]);
                          setSelected(copy.uid);
                        } : undefined}
                        onRemove={() => {
                          update(items.filter((i) => i.uid !== item.uid));
                          if (selected === item.uid) setSelected(null);
                        }} />
                    );
                  })}
                </ol>
              </SortableContext>
            </section>

            <section aria-labelledby={`${id}-properties`} className="space-y-2">
              <h3 id={`${id}-properties`} className="font-semibold">{t("properties")}</h3>
              {!selectedItem ? (
                <p className="text-ink-muted">{t("selectField")}</p>
              ) : selectedItem.element.type === "Field" ? (
                <FieldProperties key={selectedItem.uid} field={selectedItem.element.field} allFields={resolved}
                  onChange={(field) => update(items.map((i) => (i.uid === selectedItem.uid ? { ...i, element: { type: "Field", field } } : i)))} />
              ) : (
                <div className="space-y-2">
                  <p>{t("fieldSetInForm", { name: titleOf(selectedItem) })}</p>
                  <ul className="list-disc ps-6 text-sm">
                    {(setsById[selectedItem.element.fieldSetId]?.fields ?? []).map((f) => <li key={f.key}>{textIn(f.label, locale)} ({f.key})</li>)}
                  </ul>
                  <a className="text-brand underline" href={`/${locale}/settings/field-sets/${selectedItem.element.fieldSetId}`}>{t("editFieldSet")}</a>
                </div>
              )}
            </section>
          </div>
        </DndContext>
      ) : (
        <section aria-labelledby={`${id}-preview`} className="space-y-3">
          <h3 id={`${id}-preview`} className="font-semibold">{t("previewTitle")}</h3>
          <div className="flex flex-wrap gap-4">
            <span role="group" aria-label={t("previewDevice")} className="flex gap-1">
              <button type="button" className={fieldButton} aria-pressed={device === "desktop"} onClick={() => setDevice("desktop")}>{t("desktop")}</button>
              <button type="button" className={fieldButton} aria-pressed={device === "mobile"} onClick={() => setDevice("mobile")}>{t("mobile")}</button>
            </span>
            <span role="group" aria-label={t("previewLanguage")} className="flex gap-1">
              <button type="button" className={fieldButton} aria-pressed={previewLocale === "en"} onClick={() => setPreviewLocale("en")} lang="en">English</button>
              <button type="button" className={fieldButton} aria-pressed={previewLocale === "ar"} onClick={() => setPreviewLocale("ar")} lang="ar">العربية</button>
            </span>
            <button type="button" className={fieldButton} onClick={() => setPreviewChecked(true)}>{t("previewCheck")}</button>
            <button type="button" className={fieldButton} onClick={() => { setPreviewAnswers({}); setPreviewChecked(false); }}>{t("previewReset")}</button>
          </div>
          <p className="text-sm text-ink-muted">{t("previewHint")}</p>
          <div className={`mx-auto rounded-xl border border-line p-4 ${device === "mobile" ? "w-full max-w-[375px]" : "w-full"}`} data-testid={`preview-${device}`}>
            <NextIntlClientProvider locale={previewLocale} messages={previewMessages[previewLocale]}>
              <FormRenderer fields={resolved} answers={previewAnswers} onChange={setPreviewAnswers} defaults={{}}
                errors={previewChecked ? preview.errors : []} contentLocale={previewLocale} locations={locations} preview idPrefix={`${id}-preview`} />
            </NextIntlClientProvider>
          </div>
          {previewChecked && (
            <p role="status" aria-live="polite">{preview.errors.length === 0 ? t("previewOk") : t("previewProblems", { count: preview.errors.length })}</p>
          )}
        </section>
      )}
    </div>
  );
}

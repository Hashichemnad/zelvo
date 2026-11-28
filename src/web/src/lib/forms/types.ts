/**
 * Forms (task C1, module 2): the JSON shapes of the API (`/api/v1/forms`, `/field-sets`,
 * `/form-bindings`, `/forms/resolve`). The server is Zelvo.Tickets.Forms; keep the two equal.
 */

export type FieldType =
  | "Text"
  | "Number"
  | "Date"
  | "Dropdown"
  | "Checkbox"
  | "Radio"
  | "File"
  | "User"
  | "Location"
  | "Signature"
  | "Table"
  | "RichText"
  | "ConfigurationItem";

export const fieldTypes: readonly FieldType[] = [
  "Text",
  "Number",
  "Date",
  "Dropdown",
  "Checkbox",
  "Radio",
  "File",
  "User",
  "Location",
  "Signature",
  "Table",
  "RichText",
  // Task G1: a configuration item of the CMDB (module 18), or several.
  "ConfigurationItem",
];

/** Types a table column can have. */
export const columnTypes: readonly FieldType[] = ["Text", "Number", "Date", "Dropdown", "Checkbox"];

export type FieldFormat = "EmiratesId" | "Phone" | "Email" | "Iban";
export const fieldFormats: readonly FieldFormat[] = ["EmiratesId", "Phone", "Email", "Iban"];

export type ProfileSource = "DisplayName" | "Email" | "Phone" | "EmiratesId" | "EmployeeId" | "Department" | "Manager" | "Location";
export const profileSources: readonly ProfileSource[] = ["DisplayName", "Email", "Phone", "EmiratesId", "EmployeeId", "Department", "Manager", "Location"];

export type RuleAction = "Show" | "Hide" | "Require" | "ReadOnly";
export const ruleActions: readonly RuleAction[] = ["Show", "Hide", "Require", "ReadOnly"];

export type RuleMatch = "All" | "Any";

export type ConditionOperator =
  | "Equals"
  | "NotEquals"
  | "Contains"
  | "Empty"
  | "NotEmpty"
  | "GreaterThan"
  | "LessThan"
  | "Checked"
  | "NotChecked";
export const conditionOperators: readonly ConditionOperator[] = [
  "Equals",
  "NotEquals",
  "Contains",
  "Empty",
  "NotEmpty",
  "GreaterThan",
  "LessThan",
  "Checked",
  "NotChecked",
];

/** Operators that compare with a value. */
export const operatorsWithValue: ReadonlySet<ConditionOperator> = new Set(["Equals", "NotEquals", "Contains", "GreaterThan", "LessThan"]);

export interface Text {
  en?: string | null;
  ar?: string | null;
}

export interface FieldOption {
  value: string;
  label: Text;
}

export interface FieldValidation {
  min?: number | null;
  max?: number | null;
  minLength?: number | null;
  maxLength?: number | null;
  pattern?: string | null;
  format?: FieldFormat | null;
  minDate?: string | null;
  maxDate?: string | null;
  fileTypes?: string[] | null;
  maxFileSizeMb?: number | null;
  maxFiles?: number | null;
}

export interface RuleCondition {
  field: string;
  operator: ConditionOperator;
  value?: string | null;
}

export interface FieldRule {
  action: RuleAction;
  match: RuleMatch;
  conditions: RuleCondition[];
}

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export interface FormField {
  key: string;
  type: FieldType;
  label: Text;
  help?: Text | null;
  placeholder?: Text | null;
  required?: boolean;
  readOnly?: boolean;
  personalData?: boolean;
  multiline?: boolean;
  /** Dropdown and configuration item: several choices. */
  multiple?: boolean;
  /** Configuration item (task G1): the classes items may be picked from, each with the classes below it; empty: any class. */
  configurationItemClasses?: string[] | null;
  options?: FieldOption[] | null;
  /** Task K3: dropdown and radio fields take their options from this data list (its key) when the form is filled in. */
  dataList?: string | null;
  validation?: FieldValidation | null;
  defaultValue?: JsonValue;
  profileDefault?: ProfileSource | null;
  columns?: FormField[] | null;
  minRows?: number | null;
  maxRows?: number | null;
  rules?: FieldRule[] | null;
}

export type FormElement = { type: "Field"; field: FormField } | { type: "FieldSet"; fieldSetId: string };

export interface FormSchema {
  elements: FormElement[];
}

export interface FieldSetSchema {
  fields: FormField[];
}

export interface ResolvedField {
  field: FormField;
  fieldSetId?: string | null;
}

export type Answers = Record<string, JsonValue>;

export type FormState = "Draft" | "Published" | "Retired";

export interface LocalizedName {
  en: string | null;
  ar: string | null;
  missing?: string[];
}

export interface FormProblem {
  field?: string | null;
  code: string;
  message: string;
}

export interface NodeRef {
  id: string;
  name: LocalizedName;
  path: LocalizedName;
}

export interface FormBindingRef {
  id: string;
  recordType: string;
  category: NodeRef | null;
}

export interface FormSummary {
  id: string;
  key: string;
  name: LocalizedName;
  description: LocalizedName;
  state: FormState;
  hasUnpublishedChanges: boolean;
  publishedVersion: number | null;
  publishedAt: string | null;
  elementCount: number;
  usedBy: FormBindingRef[];
  updatedAt: string;
}

export interface FormDetail extends Omit<FormSummary, "elementCount"> {
  draft: FormSchema;
  draftFields: ResolvedField[];
  problems: FormProblem[];
  etag: number;
}

export interface FormUseRef {
  id: string;
  key: string;
  name: LocalizedName;
  state: FormState;
}

export interface FieldSetDetail {
  id: string;
  key: string;
  name: LocalizedName;
  description: LocalizedName;
  state: FormState;
  hasUnpublishedChanges: boolean;
  publishedVersion: number | null;
  publishedAt: string | null;
  draft: FieldSetSchema;
  published: FieldSetSchema | null;
  problems: FormProblem[];
  usedBy: FormUseRef[];
  updatedAt: string;
  etag: number;
}

export interface FormBinding {
  id: string;
  recordType: string;
  /** Task K2: the custom record type of a binding for custom records. */
  customRecordTypeId?: string | null;
  category: NodeRef | null;
  form: FormUseRef;
}

export interface FormFileRef {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  scanStatus: "Pending" | "Clean" | "Infected" | "NotScanned";
  ticketId?: string | null;
}

export interface FormReferences {
  users: Record<string, string | null>;
  locations: Record<string, LocalizedName>;
  files: Record<string, FormFileRef>;
  /** Task G1: configuration items by id, as "CI-000123 Name". */
  configurationItems?: Record<string, string> | null;
}

export interface FormRef {
  id: string;
  key: string;
  name: LocalizedName;
  description: LocalizedName;
  versionId: string;
  versionNumber: number;
}

export interface ResolvedForm {
  form: FormRef | null;
  fields: ResolvedField[];
  defaults: Answers;
  references: FormReferences;
}

export interface TicketFormView {
  form: FormRef | null;
  fields?: ResolvedField[];
  answers?: Answers;
  references?: FormReferences;
  submittedAt?: string;
}

/** An answer the server or the browser refused: field key (`table.3.column` inside tables) and code. */
export interface AnswerError {
  field: string;
  code: string;
  message?: string;
}

export interface FormUpload {
  id: string;
  field: string;
  fileName: string;
  contentType: string;
  size: number;
  scanStatus: FormFileRef["scanStatus"];
}

/** A field's label (or another text) in the language, falling back to the other one. */
export function textIn(text: Text | null | undefined, locale: "en" | "ar"): string {
  const own = text?.[locale]?.trim();
  if (own) return own;
  return text?.[locale === "ar" ? "en" : "ar"]?.trim() ?? "";
}

/** Whether the text exists only in the other language (shown with that language's lang and dir). */
export function isFallback(text: Text | null | undefined, locale: "en" | "ar"): boolean {
  return !text?.[locale]?.trim() && !!text?.[locale === "ar" ? "en" : "ar"]?.trim();
}

/** Answers that count as personal data whatever the field says (as on the server). */
export function isPersonalData(field: FormField): boolean {
  return (
    !!field.personalData ||
    field.type === "Signature" ||
    !!field.validation?.format ||
    field.profileDefault === "Email" ||
    field.profileDefault === "Phone" ||
    field.profileDefault === "EmiratesId"
  );
}

/** Fields of a form schema with field sets expanded from `fieldSets` (published fields by id). */
export function expand(schema: FormSchema, fieldSets: Record<string, FormField[]>): ResolvedField[] {
  return schema.elements.flatMap((element): ResolvedField[] =>
    element.type === "Field"
      ? [{ field: element.field, fieldSetId: null }]
      : (fieldSets[element.fieldSetId] ?? []).map((field) => ({ field, fieldSetId: element.fieldSetId })),
  );
}

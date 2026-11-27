import "server-only";
import type { AbstractIntlMessages } from "next-intl";
import { getJsonAs } from "../api";
import type { TreeNode } from "../directory";
import type { AvailableFieldSet } from "@/components/forms/FormBuilder";
import type { FieldSetDetail } from "./types";

/** The form renderer's texts in English and Arabic, so the builder can preview a form in either language. */
export async function previewMessages(): Promise<Record<"en" | "ar", AbstractIntlMessages>> {
  const [en, ar] = await Promise.all([import("../../../messages/en.json"), import("../../../messages/ar.json")]);
  return {
    // Task G1: the configuration item field's search box uses the CMDB picker's texts.
    en: { forms: { renderer: en.default.forms.renderer }, cmdb: { picker: en.default.cmdb.picker } },
    ar: { forms: { renderer: ar.default.forms.renderer }, cmdb: { picker: ar.default.cmdb.picker } },
  };
}

/** What the builder needs besides the form: published field sets and locations for the preview. */
export async function loadBuilderChoices(accessToken: string, tenant: string): Promise<{ fieldSets: AvailableFieldSet[]; locations: TreeNode[] }> {
  const [sets, locations] = await Promise.all([
    getJsonAs<FieldSetDetail[]>("/api/v1/field-sets", accessToken, tenant),
    getJsonAs<TreeNode[]>("/api/v1/locations", accessToken, tenant),
  ]);
  return {
    fieldSets: (sets ?? []).filter((s) => s.state === "Published" && s.published).map((s) => ({ id: s.id, name: s.name, fields: s.published!.fields })),
    locations: locations ?? [],
  };
}

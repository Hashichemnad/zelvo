"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { dangerButton, secondaryButton } from "@/components/directory/common";
import { Localized } from "@/components/directory/common";
import { useRouter } from "@/i18n/navigation";
import { deleteRequest, patchJson } from "@/lib/client-api";
import { textIn, type CiAttribute, type CiClass, type ConfigurationItem } from "@/lib/cmdb";
import type { GroupSummary, TreeNode } from "@/lib/directory";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { textIn as fieldText } from "@/lib/forms/types";
import type { Locale } from "@/lib/incidents";
import { CiEditor } from "./CiEditor";
import { CiStatusBadge, CmdbStatusLine, FamilyLabel, useSaver } from "./common";

/** An attribute's value as people read it: option labels, yes or no, names of users and locations. */
function AttributeValue({ attribute, display }: { attribute: CiAttribute; display: DateDisplayOptions }) {
  const t = useTranslations("cmdb.item");
  const locale = display.locale as Locale;
  const { field, value } = attribute;
  if (value === null || value === undefined || value === "") return <span className="text-ink-muted">{t("noValue")}</span>;
  const option = (v: unknown) => fieldText(field.options?.find((o) => o.value === v)?.label, locale) || String(v);
  switch (field.type) {
    case "Checkbox":
      return <>{value === true ? t("yes") : t("no")}</>;
    case "Dropdown":
    case "Radio":
      return <>{Array.isArray(value) ? value.map(option).join(", ") : option(value)}</>;
    case "Number":
      return <>{typeof value === "number" ? formatNumber(value, locale, display.digits) : String(value)}</>;
    case "User":
    case "Location":
      return <>{attribute.display ? textIn(attribute.display, locale) : String(value)}</>;
    default:
      return <span dir="auto" className="whitespace-pre-wrap break-words">{String(value)}</span>;
  }
}

/**
 * A configuration item's facts and attributes (task G1), with changing, retiring and deleting
 * for holders of cmdb.manage. An item tickets refer to cannot be deleted; it is retired instead.
 */
export function CiItemView({
  initial,
  classes,
  groups,
  locations,
  display,
  canManage,
  csrfToken,
}: {
  initial: ConfigurationItem;
  classes: CiClass[];
  groups: GroupSummary[] | null;
  locations: TreeNode[];
  display: DateDisplayOptions;
  canManage: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("cmdb.item");
  const locale = display.locale as Locale;
  const router = useRouter();
  const [item, setItem] = useState(initial);
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const { status, run, busy } = useSaver();

  if (editing) {
    return (
      <CiEditor item={item} classes={classes} groups={groups} locations={locations} csrfToken={csrfToken}
        onCancel={() => setEditing(false)}
        onSaved={(saved) => {
          setItem(saved);
          setEditing(false);
          router.refresh();
        }} />
    );
  }

  return (
    <div className="space-y-6">
      <dl className="grid gap-x-4 gap-y-2 sm:grid-cols-[minmax(0,12rem)_minmax(0,1fr)]" data-testid="ci-facts">
        <dt className="font-semibold">{t("number")}</dt>
        <dd className="font-mono" dir="ltr">{item.number}</dd>
        <dt className="font-semibold">{t("class")}</dt>
        <dd>
          <Localized text={item.class.path} locale={locale} /> · <FamilyLabel family={item.class.family} />
        </dd>
        <dt className="font-semibold">{t("status")}</dt>
        <dd><CiStatusBadge status={item.status} /></dd>
        <dt className="font-semibold">{t("ownerGroup")}</dt>
        <dd>{item.ownerGroup ? <Localized text={item.ownerGroup.name} locale={locale} /> : t("none")}</dd>
        <dt className="font-semibold">{t("location")}</dt>
        <dd>{item.location ? <Localized text={item.location.path} locale={locale} /> : t("none")}</dd>
        <dt className="font-semibold">{t("description")}</dt>
        <dd className="whitespace-pre-wrap">{textIn(item.description, locale) ? <Localized text={item.description} locale={locale} /> : t("none")}</dd>
        <dt className="font-semibold">{t("updated")}</dt>
        <dd>{formatDateTime(new Date(item.updatedAt), display)}</dd>
      </dl>

      <section aria-labelledby="ci-attributes-heading" className="space-y-2">
        <h2 id="ci-attributes-heading" className="text-xl font-bold">{t("attributes")}</h2>
        {item.attributes.length === 0 ? (
          <p>{t("noAttributes")}</p>
        ) : (
          <dl className="grid gap-x-4 gap-y-2 sm:grid-cols-[minmax(0,12rem)_minmax(0,1fr)]" data-testid="ci-attributes">
            {item.attributes.map((a) => (
              <div key={a.field.key} className="contents">
                <dt className="font-semibold">{fieldText(a.field.label, locale) || a.field.key}</dt>
                <dd className="min-w-0"><AttributeValue attribute={a} display={display} /></dd>
              </div>
            ))}
          </dl>
        )}
        {Object.keys(item.stale).length > 0 && <p className="text-sm text-ink-muted">{t("stale", { count: Object.keys(item.stale).length })}</p>}
      </section>

      {canManage && (
        <div className="flex flex-wrap gap-2">
          <button type="button" className={secondaryButton} onClick={() => setEditing(true)}>{t("edit")}</button>
          {item.status !== "Retired" && (
            <button type="button" className={secondaryButton} disabled={busy}
              onClick={() => void run(async () => {
                setItem(await patchJson<ConfigurationItem>(`/v1/cmdb/items/${item.id}`, { status: "Retired" }, csrfToken, { "If-Match": item.etag }));
                router.refresh();
              })}>
              {t("retire")}
            </button>
          )}
          {!confirmDelete ? (
            <button type="button" className={dangerButton} disabled={busy || item.ticketCount > 0} aria-describedby={item.ticketCount > 0 ? "ci-delete-hint" : undefined}
              onClick={() => setConfirmDelete(true)}>
              {t("delete")}
            </button>
          ) : (
            <span className="flex flex-wrap items-center gap-2" role="group" aria-label={t("deleteConfirm", { number: item.number })}>
              <span>{t("deleteConfirm", { number: item.number })}</span>
              <button type="button" className={dangerButton} disabled={busy}
                onClick={() => void run(async () => {
                  await deleteRequest(`/v1/cmdb/items/${item.id}`, csrfToken);
                  router.push("/cmdb");
                })}>
                {t("deleteYes")}
              </button>
              <button type="button" className={secondaryButton} onClick={() => setConfirmDelete(false)}>{t("deleteNo")}</button>
            </span>
          )}
          {item.ticketCount > 0 && <p id="ci-delete-hint" className="w-full text-sm text-ink-muted">{t("deleteHint")}</p>}
        </div>
      )}
      <CmdbStatusLine status={status} />
    </div>
  );
}

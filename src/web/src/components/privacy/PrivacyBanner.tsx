import { getTranslations } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getRequestAuthConfig } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";
import type { MyPrivacy, PrivacyNotice } from "@/lib/privacy";

/**
 * Task F5 (decided 2026-11-25): signed-in people accept the published privacy notice at their
 * first visit and again after a new version. Until they do, every page shows this banner leading
 * to Account → Privacy, where they read and accept it; nothing else is blocked, so staff can still
 * reach urgent work.
 */
export async function PrivacyBanner({ notice }: { notice: PrivacyNotice | null }) {
  if (!notice) return null;
  const config = await getRequestAuthConfig();
  const session = await getSession(config).catch(() => null);
  if (!session) return null;
  const mine = await getJsonAs<MyPrivacy>("/api/v1/me/privacy", session.data.accessToken, config.tenant);
  if (!mine || mine.noticeAccepted || !mine.notice) return null;
  const t = await getTranslations("privacy.banner");
  return (
    <div role="region" aria-label={t("label")} className="border-b-2 border-brand bg-surface-muted" data-testid="privacy-banner">
      <p className="mx-auto flex max-w-5xl flex-wrap items-center gap-3 px-4 py-3">
        <span>{mine.notice.version > 1 ? t("updated") : t("first")}</span>
        <Link href="/account/privacy" className="font-semibold text-brand underline">{t("review")}</Link>
      </p>
    </div>
  );
}

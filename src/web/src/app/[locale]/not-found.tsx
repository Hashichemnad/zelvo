import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";

export default function NotFound() {
  const t = useTranslations("notFound");

  return (
    <div className="space-y-4">
      <h1 className="text-3xl font-bold">{t("title")}</h1>
      <p>{t("description")}</p>
      <Link href="/" className="font-semibold text-brand underline">
        {t("home")}
      </Link>
    </div>
  );
}

import { notFound } from "next/navigation";

/** Sends unknown paths under a locale to the translated not-found page. */
export default function CatchAll() {
  notFound();
}

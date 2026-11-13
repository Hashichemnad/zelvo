import createMiddleware from "next-intl/middleware";
import { routing } from "./i18n/routing";

export default createMiddleware(routing);

export const config = {
  // Everything except API routes, the health endpoint, Next internals and files with an extension.
  matcher: "/((?!api|healthz|_next|_vercel|.*\\..*).*)",
};

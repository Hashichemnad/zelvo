import createMiddleware from "next-intl/middleware";
import { routing } from "./i18n/routing";

export default createMiddleware(routing);

export const config = {
  // Everything except API routes, sign-in routes (/auth/...), the API proxy (/bff/...), the health
  // endpoint, Next internals and files with an extension.
  matcher: "/((?!api|auth/|bff/|healthz|_next|_vercel|.*\\..*).*)",
};

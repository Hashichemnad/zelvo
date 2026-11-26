import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const withNextIntl = createNextIntlPlugin("./src/i18n/request.ts");

const securityHeaders = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=()" },
];

const nextConfig: NextConfig = {
  output: "standalone",
  poweredByHeader: false,
  // Customers receive minified builds only, never source (module 12): no browser source maps.
  productionBrowserSourceMaps: false,
  // Release images (task F6) are built with ZELVO_RELEASE=true; the value is compiled in, so the
  // release integrity check cannot be switched off by the environment at run time.
  env: { ZELVO_RELEASE_BUILD: process.env.ZELVO_RELEASE === "true" ? "true" : "false" },
  reactStrictMode: true,
  async headers() {
    return [
      { source: "/:path*", headers: securityHeaders },
      // Pages are never framed, except the chat widget, which the proxy limits to its websites (task E2).
      // Task F7: a baseline policy for responses the proxy does not see (route handlers under /auth
      // and /bff, files); pages get the proxy's full policy with a nonce as well (lib/csp.ts), and
      // browsers apply both, so this one only adds what both allow.
      {
        source: "/((?!chat-widget/).*)",
        headers: [
          { key: "X-Frame-Options", value: "DENY" },
          { key: "Content-Security-Policy", value: "object-src 'none'; base-uri 'none'; frame-ancestors 'none'" },
        ],
      },
    ];
  },
};

export default withNextIntl(nextConfig);

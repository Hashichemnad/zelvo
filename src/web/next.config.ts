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
  reactStrictMode: true,
  async headers() {
    return [
      { source: "/:path*", headers: securityHeaders },
      // Pages are never framed, except the chat widget, which the proxy limits to its websites (task E2).
      { source: "/((?!chat-widget/).*)", headers: [{ key: "X-Frame-Options", value: "DENY" }] },
    ];
  },
};

export default withNextIntl(nextConfig);

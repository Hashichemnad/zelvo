# syntax=docker/dockerfile:1
# Build context: src/web.

FROM node:24-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

FROM node:24-alpine AS build
WORKDIR /app
ENV NEXT_TELEMETRY_DISABLED=1
# Release images (task F6): compiled into the build, see next.config.ts.
ARG ZELVO_RELEASE=false
ENV ZELVO_RELEASE=$ZELVO_RELEASE
COPY --from=deps /app/node_modules ./node_modules
COPY . .
# Production builds are minified and ship no source maps (module 12, task F6).
RUN npm run build && find .next -name '*.map' -delete

FROM node:24-alpine AS runtime
# Apply Alpine security fixes, and remove the package managers: the runtime only needs node,
# and npm's bundled dependencies otherwise show up in the image scan.
RUN apk upgrade --no-cache \
 && rm -rf /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/corepack /opt/yarn-* \
           /usr/local/bin/npm /usr/local/bin/npx /usr/local/bin/corepack /usr/local/bin/yarn /usr/local/bin/yarnpkg
WORKDIR /app
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    HOSTNAME=0.0.0.0 \
    PORT=3000
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
COPY --from=build --chown=node:node /app/public ./public
USER node
EXPOSE 3000
CMD ["node", "server.js"]

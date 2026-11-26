# syntax=docker/dockerfile:1
# Build context: repository root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/worker/Zelvo.Worker/Zelvo.Worker.csproj src/worker/Zelvo.Worker/
COPY src/api/Zelvo.Licensing.Abstractions/Zelvo.Licensing.Abstractions.csproj src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing/Zelvo.Licensing.csproj src/api/Zelvo.Licensing/
COPY src/api/Zelvo.Data/Zelvo.Data.csproj src/api/Zelvo.Data/
COPY src/api/Zelvo.Search/Zelvo.Search.csproj src/api/Zelvo.Search/
COPY src/api/Zelvo.Tickets/Zelvo.Tickets.csproj src/api/Zelvo.Tickets/
COPY src/api/Zelvo.Workflows/Zelvo.Workflows.csproj src/api/Zelvo.Workflows/
COPY src/api/Zelvo.Integrations/Zelvo.Integrations.csproj src/api/Zelvo.Integrations/
COPY src/api/Zelvo.Notifications/Zelvo.Notifications.csproj src/api/Zelvo.Notifications/
COPY src/api/Zelvo.Channels/Zelvo.Channels.csproj src/api/Zelvo.Channels/
COPY src/api/Zelvo.Connectors/Zelvo.Connectors.csproj src/api/Zelvo.Connectors/
COPY src/api/Zelvo.Ai/Zelvo.Ai.csproj src/api/Zelvo.Ai/
COPY src/api/Zelvo.Secrets/Zelvo.Secrets.csproj src/api/Zelvo.Secrets/
COPY src/api/Zelvo.Observability/Zelvo.Observability.csproj src/api/Zelvo.Observability/
RUN dotnet restore src/worker/Zelvo.Worker/Zelvo.Worker.csproj
COPY src/worker/Zelvo.Worker/ src/worker/Zelvo.Worker/
COPY src/api/Zelvo.Licensing.Abstractions/ src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing/ src/api/Zelvo.Licensing/
COPY src/api/Zelvo.Data/ src/api/Zelvo.Data/
COPY src/api/Zelvo.Search/ src/api/Zelvo.Search/
COPY src/api/Zelvo.Tickets/ src/api/Zelvo.Tickets/
COPY src/api/Zelvo.Workflows/ src/api/Zelvo.Workflows/
COPY src/api/Zelvo.Integrations/ src/api/Zelvo.Integrations/
COPY src/api/Zelvo.Notifications/ src/api/Zelvo.Notifications/
COPY src/api/Zelvo.Channels/ src/api/Zelvo.Channels/
COPY src/api/Zelvo.Connectors/ src/api/Zelvo.Connectors/
COPY src/api/Zelvo.Ai/ src/api/Zelvo.Ai/
COPY src/api/Zelvo.Secrets/ src/api/Zelvo.Secrets/
COPY src/api/Zelvo.Observability/ src/api/Zelvo.Observability/
# Release builds (task F6; the release pipeline sets these): ZELVO_RELEASE=true is the release
# licensing build, ZELVO_BUILD_WATERMARK the customer's watermark for a per-customer build, and
# ZELVO_OBFUSCATE=true obfuscates Zelvo's assemblies (build/obfuscar) and removes their symbols.
ARG ZELVO_RELEASE=false
ARG ZELVO_BUILD_WATERMARK=
ARG ZELVO_OBFUSCATE=false
RUN dotnet publish src/worker/Zelvo.Worker/Zelvo.Worker.csproj -c Release -o /app --no-restore -p:UseAppHost=false \
    -p:ZelvoRelease="$ZELVO_RELEASE" -p:ZelvoBuildWatermark="$ZELVO_BUILD_WATERMARK"
COPY dotnet-tools.json ./
COPY build/obfuscar/ build/obfuscar/
RUN mkdir -p /obfuscation && if [ "$ZELVO_OBFUSCATE" = "true" ]; then \
      build/obfuscar/obfuscate.sh /app /obfuscation/Mapping.txt; fi

# The obfuscation mapping, for the release pipeline only (docker build --target obfuscation-map
# --output ...); it is never part of the image.
FROM scratch AS obfuscation-map
COPY --from=build /obfuscation/ /

# Fonts the branding can choose (task F1), from the same npm packages as the web app, for PDF exports.
FROM node:24-alpine AS fonts
WORKDIR /fonts
RUN npm pack --silent @fontsource/ibm-plex-sans-arabic@5 @fontsource/cairo@5 @fontsource/tajawal@5 @fontsource/almarai@5 \
    && mkdir out \
    && for package in *.tgz; do mkdir -p x && tar -xzf "$package" -C x \
         && name=$(basename "$package" .tgz | sed -E 's/^fontsource-//; s/-[0-9.]+$//') \
         && for weight in 400 700; do for subset in arabic latin; do \
              f=$(ls x/package/files/*-"$subset"-"$weight"-normal.woff2 2>/dev/null | head -n 1); \
              if [ -n "$f" ]; then cp "$f" "out/$name-$subset-$weight.woff2"; fi; done; done; \
         rm -rf x; done

# Chromium's headless shell for branded PDF exports (task F1, product owner decision 2026-11-24),
# the build Playwright publishes, pinned to the version the end-to-end tests use. Ubuntu's own
# `chromium` package is only a wrapper that installs the snap, which does not run in a container.
FROM node:24-bookworm-slim AS chromium
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN npx --yes playwright-core@1.65.1 install chromium-headless-shell \
    && mv /ms-playwright/chromium_headless_shell-*/chrome-headless-shell-* /chromium \
    && test -x /chromium/chrome-headless-shell

# The ASP.NET Core runtime: Elsa (task C3, through Zelvo.Workflows) needs the Microsoft.AspNetCore.App framework.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# The libraries the headless shell needs on Ubuntu 24.04 (Playwright's list) and fonts with
# Arabic letters as a fallback.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 libcairo2 libcups2t64 \
       libdbus-1-3 libdrm2 libgbm1 libglib2.0-0t64 libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1 \
       libxcomposite1 libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 fontconfig fonts-noto-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=chromium /chromium /opt/chromium
COPY --from=fonts /fonts/out /usr/share/zelvo/fonts
COPY --from=build /app .
ENV Pdf__ChromiumPath=/opt/chromium/chrome-headless-shell Pdf__FontsDirectory=/usr/share/zelvo/fonts
USER $APP_UID
ENTRYPOINT ["dotnet", "Zelvo.Worker.dll"]

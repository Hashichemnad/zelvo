# syntax=docker/dockerfile:1
# Build context: repository root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/worker/Zelvo.Worker/Zelvo.Worker.csproj src/worker/Zelvo.Worker/
COPY src/api/Zelvo.Licensing.Abstractions/Zelvo.Licensing.Abstractions.csproj src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing.Development/Zelvo.Licensing.Development.csproj src/api/Zelvo.Licensing.Development/
COPY src/api/Zelvo.Data/Zelvo.Data.csproj src/api/Zelvo.Data/
COPY src/api/Zelvo.Search/Zelvo.Search.csproj src/api/Zelvo.Search/
COPY src/api/Zelvo.Tickets/Zelvo.Tickets.csproj src/api/Zelvo.Tickets/
COPY src/api/Zelvo.Integrations/Zelvo.Integrations.csproj src/api/Zelvo.Integrations/
COPY src/api/Zelvo.Notifications/Zelvo.Notifications.csproj src/api/Zelvo.Notifications/
COPY src/api/Zelvo.Channels/Zelvo.Channels.csproj src/api/Zelvo.Channels/
COPY src/api/Zelvo.Ai/Zelvo.Ai.csproj src/api/Zelvo.Ai/
COPY src/api/Zelvo.Secrets/Zelvo.Secrets.csproj src/api/Zelvo.Secrets/
COPY src/api/Zelvo.Observability/Zelvo.Observability.csproj src/api/Zelvo.Observability/
RUN dotnet restore src/worker/Zelvo.Worker/Zelvo.Worker.csproj
COPY src/worker/Zelvo.Worker/ src/worker/Zelvo.Worker/
COPY src/api/Zelvo.Licensing.Abstractions/ src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing.Development/ src/api/Zelvo.Licensing.Development/
COPY src/api/Zelvo.Data/ src/api/Zelvo.Data/
COPY src/api/Zelvo.Search/ src/api/Zelvo.Search/
COPY src/api/Zelvo.Tickets/ src/api/Zelvo.Tickets/
COPY src/api/Zelvo.Integrations/ src/api/Zelvo.Integrations/
COPY src/api/Zelvo.Notifications/ src/api/Zelvo.Notifications/
COPY src/api/Zelvo.Channels/ src/api/Zelvo.Channels/
COPY src/api/Zelvo.Ai/ src/api/Zelvo.Ai/
COPY src/api/Zelvo.Secrets/ src/api/Zelvo.Secrets/
COPY src/api/Zelvo.Observability/ src/api/Zelvo.Observability/
RUN dotnet publish src/worker/Zelvo.Worker/Zelvo.Worker.csproj -c Release -o /app --no-restore -p:UseAppHost=false

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

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
# Branded PDF exports (task F1, product owner decision 2026-11-24): headless Chromium and
# fonts with Arabic letters as a fallback.
RUN apt-get update \
    && apt-get install -y --no-install-recommends chromium fonts-noto-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=fonts /fonts/out /usr/share/zelvo/fonts
COPY --from=build /app .
ENV Pdf__ChromiumPath=/usr/bin/chromium Pdf__FontsDirectory=/usr/share/zelvo/fonts
USER $APP_UID
ENTRYPOINT ["dotnet", "Zelvo.Worker.dll"]

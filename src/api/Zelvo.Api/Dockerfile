# syntax=docker/dockerfile:1
# Build context: repository root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/api/Zelvo.Api/Zelvo.Api.csproj src/api/Zelvo.Api/
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
COPY src/api/Zelvo.Licensing.Abstractions/Zelvo.Licensing.Abstractions.csproj src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing/Zelvo.Licensing.csproj src/api/Zelvo.Licensing/
RUN dotnet restore src/api/Zelvo.Api/Zelvo.Api.csproj
COPY src/api/Zelvo.Api/ src/api/Zelvo.Api/
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
COPY src/api/Zelvo.Licensing.Abstractions/ src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing/ src/api/Zelvo.Licensing/
RUN dotnet publish src/api/Zelvo.Api/Zelvo.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Task F4: the updates volume (packages and the inbox) starts owned by the API's user.
RUN mkdir -p /var/lib/zelvo/updates/packages /var/lib/zelvo/updates/inbox /var/lib/zelvo/updates/status \
    && chown -R "$APP_UID" /var/lib/zelvo
# Non-root user provided by the .NET base image.
USER $APP_UID
# `migrate` as the first argument applies database migrations and exits (see MigrateCommand).
ENTRYPOINT ["dotnet", "Zelvo.Api.dll"]

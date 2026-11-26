# syntax=docker/dockerfile:1
# Build context: repository root. Zelvo's updater service (task F4).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/updater/Zelvo.Updater/Zelvo.Updater.csproj src/updater/Zelvo.Updater/
COPY src/api/Zelvo.Licensing.Abstractions/Zelvo.Licensing.Abstractions.csproj src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing/Zelvo.Licensing.csproj src/api/Zelvo.Licensing/
COPY src/api/Zelvo.Data/Zelvo.Data.csproj src/api/Zelvo.Data/
COPY src/api/Zelvo.Observability/Zelvo.Observability.csproj src/api/Zelvo.Observability/
RUN dotnet restore src/updater/Zelvo.Updater/Zelvo.Updater.csproj
COPY src/updater/Zelvo.Updater/ src/updater/Zelvo.Updater/
COPY src/api/Zelvo.Licensing.Abstractions/ src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing/ src/api/Zelvo.Licensing/
COPY src/api/Zelvo.Data/ src/api/Zelvo.Data/
COPY src/api/Zelvo.Observability/ src/api/Zelvo.Observability/
RUN dotnet publish src/updater/Zelvo.Updater/Zelvo.Updater.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# The Docker command line and its Compose plugin (static binaries) from Docker's official images.
FROM docker:29.9.3-cli AS docker
FROM docker/compose-bin:v5.8.0 AS compose

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=docker /usr/local/bin/docker /usr/local/bin/docker
COPY --from=compose /docker-compose /usr/local/lib/docker/cli-plugins/docker-compose
COPY --from=build /app .
# The updater drives Docker through its socket, which needs root in this container. It listens on
# no port; see docs/architecture/updates.md for what it does and does not do.
USER root
ENTRYPOINT ["dotnet", "Zelvo.Updater.dll"]

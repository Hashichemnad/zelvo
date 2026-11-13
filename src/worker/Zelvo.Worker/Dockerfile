# syntax=docker/dockerfile:1
# Build context: repository root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/worker/Zelvo.Worker/Zelvo.Worker.csproj src/worker/Zelvo.Worker/
COPY src/api/Zelvo.Licensing.Abstractions/Zelvo.Licensing.Abstractions.csproj src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing.Development/Zelvo.Licensing.Development.csproj src/api/Zelvo.Licensing.Development/
RUN dotnet restore src/worker/Zelvo.Worker/Zelvo.Worker.csproj
COPY src/worker/Zelvo.Worker/ src/worker/Zelvo.Worker/
COPY src/api/Zelvo.Licensing.Abstractions/ src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing.Development/ src/api/Zelvo.Licensing.Development/
RUN dotnet publish src/worker/Zelvo.Worker/Zelvo.Worker.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:11.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Zelvo.Worker.dll"]

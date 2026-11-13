# syntax=docker/dockerfile:1
# Build context: repository root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/api/Zelvo.Api/Zelvo.Api.csproj src/api/Zelvo.Api/
COPY src/api/Zelvo.Licensing.Abstractions/Zelvo.Licensing.Abstractions.csproj src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing.Development/Zelvo.Licensing.Development.csproj src/api/Zelvo.Licensing.Development/
RUN dotnet restore src/api/Zelvo.Api/Zelvo.Api.csproj
COPY src/api/Zelvo.Api/ src/api/Zelvo.Api/
COPY src/api/Zelvo.Licensing.Abstractions/ src/api/Zelvo.Licensing.Abstractions/
COPY src/api/Zelvo.Licensing.Development/ src/api/Zelvo.Licensing.Development/
RUN dotnet publish src/api/Zelvo.Api/Zelvo.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Non-root user provided by the .NET base image.
USER $APP_UID
ENTRYPOINT ["dotnet", "Zelvo.Api.dll"]

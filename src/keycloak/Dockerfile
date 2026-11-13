# Zelvo's Keycloak: the upstream image plus Zelvo's extension (email one-time code sign-in and the
# "zelvo" login and email theme). The extension's tests run while the image is built.

FROM maven:3.9-eclipse-temurin-21 AS build
WORKDIR /src
COPY pom.xml ./
RUN mvn -B -q dependency:go-offline
COPY src ./src
RUN mvn -B -q verify

# Keep the version in step with keycloak.version in pom.xml.
FROM keycloak/keycloak:26.7
COPY --from=build /src/target/zelvo-keycloak-extensions-*.jar /opt/keycloak/providers/zelvo-keycloak-extensions.jar

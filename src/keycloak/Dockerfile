# Zelvo's Keycloak: the upstream image plus Zelvo's extension (email one-time code sign-in and the
# "zelvo" login and email theme). The extension's tests run while the image is built.

FROM maven:3.9-eclipse-temurin-26 AS build
WORKDIR /src
COPY pom.xml ./
RUN mvn -B -q dependency:go-offline
COPY src ./src
RUN mvn -B -q verify

# Keep the version in step with keycloak.version in pom.xml.
FROM keycloak/keycloak:26.7 AS keycloak

# Operating system security updates. The upstream image (Red Hat UBI 9 micro) has no package
# manager and can lag behind fixes, so its file system is upgraded from a full UBI 9 image using
# the image's own package database (the image scan then sees the real, updated versions).
FROM redhat/ubi9:9.8 AS os-updates
COPY --from=keycloak / /mnt/rootfs
RUN dnf upgrade --installroot /mnt/rootfs --releasever 9 --setopt install_weak_deps=false --nodocs -y \
 && dnf clean all --installroot /mnt/rootfs

# Same file system, settings and user as the upstream image.
FROM scratch
COPY --from=os-updates /mnt/rootfs /
COPY --from=build --chown=1000:0 /src/target/zelvo-keycloak-extensions-*.jar /opt/keycloak/providers/zelvo-keycloak-extensions.jar
ENV LANG=en_US.UTF-8 \
    KC_RUN_IN_CONTAINER=true
USER 1000
EXPOSE 8080 8443 9000
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]

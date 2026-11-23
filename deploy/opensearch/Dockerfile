# syntax=docker/dockerfile:1
# Zelvo's OpenSearch: the official image plus the official ICU analysis plugin, which the Arabic
# and English analysers need (Unicode normalisation and folding: diacritics, letter forms,
# Arabic-Indic digits). See docs/architecture/search.md.
#
# The plugin is downloaded from the OpenSearch artifact repository by default. For mirrored or
# offline builds, put the plugin zip at plugins/analysis-icu.zip (git-ignored); it must be the
# build for this exact OpenSearch version.

FROM opensearchproject/opensearch:3.10.0
COPY plugins/ /tmp/zelvo-plugins/
RUN if [ -f /tmp/zelvo-plugins/analysis-icu.zip ]; then \
      /usr/share/opensearch/bin/opensearch-plugin install --batch file:///tmp/zelvo-plugins/analysis-icu.zip; \
    else \
      /usr/share/opensearch/bin/opensearch-plugin install --batch analysis-icu; \
    fi

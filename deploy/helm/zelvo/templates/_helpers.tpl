{{/* Names and labels. */}}
{{- define "zelvo.fullname" -}}{{ .Release.Name | trunc 40 | trimSuffix "-" }}{{- end }}

{{- define "zelvo.labels" -}}
app.kubernetes.io/part-of: zelvo
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ include "zelvo.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version | replace "+" "_" }}
{{- end }}

{{- define "zelvo.selector" -}}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/name: {{ .name }}
{{- end }}

{{- define "zelvo.tag" -}}{{ .Values.image.tag | default .Chart.AppVersion }}{{- end }}

{{/* A Zelvo image: registry/prefix+name:tag. */}}
{{- define "zelvo.image" -}}
{{ .root.Values.image.registry }}/{{ .root.Values.image.prefix }}{{ .name }}:{{ include "zelvo.tag" .root }}
{{- end }}

{{- define "zelvo.required" -}}
{{- $_ := required "publicHost is required" .Values.publicHost -}}
{{- $_ := required "postgres.host is required" .Values.postgres.host -}}
{{- $_ := required "redis.endpoint is required" .Values.redis.endpoint -}}
{{- $_ := required "rabbitmq.host is required" .Values.rabbitmq.host -}}
{{- $_ := required "opensearch.url is required" .Values.opensearch.url -}}
{{- $_ := required "smtp.host is required" .Values.smtp.host -}}
{{- $_ := required "smtp.from is required" .Values.smtp.from -}}
{{- $_ := required "tenant.nameEn is required" .Values.tenant.nameEn -}}
{{- $_ := required "tenant.nameAr is required" .Values.tenant.nameAr -}}
{{- $_ := required "release.manifest is required (--set-file release.manifest=release/release-manifest.json)" .Values.release.manifest -}}
{{- $_ := required "release.signature is required (--set-file release.signature=release/release-manifest.json.sig)" .Values.release.signature -}}
{{- end }}

{{/* The cluster id of the license fingerprint: the value, else the kube-system namespace's uid. */}}
{{- define "zelvo.clusterId" -}}
{{- if .Values.licensing.clusterId -}}
{{ .Values.licensing.clusterId }}
{{- else -}}
{{- $ns := lookup "v1" "Namespace" "" "kube-system" -}}
{{- if $ns }}{{ $ns.metadata.uid }}{{ end -}}
{{- end -}}
{{- end }}

{{/* Restricted pod security (Pod Security Standards "restricted"). */}}
{{- define "zelvo.podSecurity" -}}
runAsNonRoot: true
seccompProfile: { type: RuntimeDefault }
{{- end }}

{{- define "zelvo.containerSecurity" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities: { drop: [ALL] }
{{- end }}

{{- define "zelvo.secretEnv" -}}
- name: {{ .env }}
  valueFrom: { secretKeyRef: { name: {{ .root.Values.existingSecret }}, key: {{ .key }} } }
{{- end }}

{{/* Settings every .NET service shares (non-secret ones from the ConfigMap). */}}
{{- define "zelvo.dotnetEnv" -}}
- name: DB_APP_PASSWORD
  valueFrom: { secretKeyRef: { name: {{ .Values.existingSecret }}, key: postgresAppPassword } }
- name: ConnectionStrings__Postgres
  value: "Host={{ .Values.postgres.host }};Port={{ .Values.postgres.port }};Database={{ .Values.postgres.database }};Username={{ .Values.postgres.appUser }};Password=$(DB_APP_PASSWORD);SSL Mode={{ .Values.postgres.sslMode }}"
{{- end }}

{{- define "zelvo.commonVolumes" -}}
- name: tmp
  emptyDir: {}
- name: release
  configMap: { name: {{ include "zelvo.fullname" . }}-release }
- name: ca
  secret: { secretName: {{ .Values.rabbitmq.caSecret }}, optional: true }
{{- if eq .Values.secrets.provider "File" }}
- name: master-key
  secret: { secretName: {{ .Values.existingSecret }}, items: [{ key: masterKey, path: master.key }], defaultMode: 0400 }
{{- end }}
{{- end }}

{{- define "zelvo.commonMounts" -}}
- { name: tmp, mountPath: /tmp }
- { name: release, mountPath: /etc/zelvo/release, readOnly: true }
- { name: ca, mountPath: /run/certs, readOnly: true }
{{- if eq .Values.secrets.provider "File" }}
- { name: master-key, mountPath: /run/secrets, readOnly: true }
{{- end }}
{{- end }}

{{/* Npgsql's SSL Mode as the PostgreSQL JDBC driver's sslmode (Keycloak). */}}
{{- define "zelvo.jdbcSslMode" -}}
{{- $mode := .Values.postgres.sslMode | lower -}}
{{- if eq $mode "verifyfull" }}verify-full{{ else if eq $mode "verifyca" }}verify-ca{{ else }}{{ $mode }}{{ end -}}
{{- end }}

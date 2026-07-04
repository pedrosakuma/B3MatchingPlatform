{{/*
Chart name, sanitized for k8s object names.
*/}}
{{- define "b3-matching.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Fully-qualified app name. Honours .Values.fullnameOverride; otherwise uses the
release name, collapsing the common `<release>-<chart>` duplication so a
`helm install matching ...` yields `matching` rather than `matching-b3-matching`.
*/}}
{{- define "b3-matching.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "b3-matching.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Selector labels — the load-bearing app identity. `app.kubernetes.io/name:
matching` is what the headless Service selector, the podAffinity target and the
NetworkPolicy podSelectors all key on across the b3-prod family, so keep it
stable (do NOT fold in the release name here).
*/}}
{{- define "b3-matching.selectorLabels" -}}
app.kubernetes.io/name: matching
{{- end -}}

{{/*
Common labels applied to every object.
*/}}
{{- define "b3-matching.labels" -}}
helm.sh/chart: {{ include "b3-matching.chart" . }}
{{ include "b3-matching.selectorLabels" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/part-of: b3-sim
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
{{- with .Values.commonLabels }}
{{ toYaml . }}
{{- end }}
{{- end -}}

{{/*
Resolve the container image reference. `image.digest` (@sha256:) wins over
`image.tag`; `image.tag` falls back to the chart appVersion.
*/}}
{{- define "b3-matching.image" -}}
{{- $repo := .Values.image.repository -}}
{{- if .Values.image.digest -}}
{{- printf "%s@%s" $repo .Values.image.digest -}}
{{- else -}}
{{- printf "%s:%s" $repo (.Values.image.tag | default .Chart.AppVersion) -}}
{{- end -}}
{{- end -}}

{{/*
ConfigMap name holding the bridge config + instruments.
*/}}
{{- define "b3-matching.configMapName" -}}
{{- printf "%s-config" (include "b3-matching.fullname" .) -}}
{{- end -}}

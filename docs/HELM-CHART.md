# Helm chart — `b3-matching` (OCI)

M3 of the deploy-topology RFC (#557 / #547). The per-service chart is **Layer-1**
and lives + publishes here, next to the `b3-matching` image. `b3deploy`
references it as `chart@version` + per-env values — no vendored manifests.

- Chart source: [`deploy/charts/b3-matching`](../deploy/charts/b3-matching)
- Image: `ghcr.io/pedrosakuma/b3-matching`
- Chart OCI: `oci://ghcr.io/pedrosakuma/charts/b3-matching`

The chart was ported verbatim from the validated-on-AKS manifest
`b3deploy@3b4c1d9 → envs/prod/matching.yaml` (+ `envs/prod/config/`), with the
env-specific bits lifted to `values.yaml` and the marketdata target templated.

## Versioning contract

- Chart `version` is **SemVer**, released **in lockstep with the image**.
- Chart `appVersion` is the **image version it was validated against**.
- `b3deploy` pins `chart@version` next to `image@sha256:` per environment.

Bump `Chart.yaml: version` in the same PR that ships a chart change intended for
release. The publish job is idempotent — it skips when that version is already
in the registry — so a merge without a bump republishes nothing.

## CI: `helm.yml`

| Job | When | What |
| --- | --- | --- |
| `lint` | every PR + push touching `deploy/charts/**` | `helm lint`, `helm template` (defaults + overrides), `kubeconform` on the rendered manifests |
| `publish` | push to `main`, tags `v*`, manual | `helm package` + `helm push oci://ghcr.io/<owner>/charts` using the GHCR token, gated on the version not already being published |

## Publish manually

```bash
helm registry login ghcr.io -u <user> --password-stdin   # GHCR PAT/token
helm package deploy/charts/b3-matching --destination /tmp/pkg
helm push /tmp/pkg/b3-matching-<version>.tgz oci://ghcr.io/pedrosakuma/charts
```

## Consume from `b3deploy`

```bash
helm pull oci://ghcr.io/pedrosakuma/charts/b3-matching --version <version>
# or reference chart@version from an env values file; b3deploy overrides
# image.digest, resources, persistence.storageClassName, marketData.host,
# colocation.* per environment.
```

See the [chart README](../deploy/charts/b3-matching/README.md) for the full
values surface and load-bearing invariants (unicast DNS abort, single-writer
WAL, co-location).

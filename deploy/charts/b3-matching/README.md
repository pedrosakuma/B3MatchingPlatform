# b3-matching Helm chart

Layer-1 component chart for the **B3 exchange-core matching platform** (FIXP
EntryPoint venue + UMDF unicast emitter). Published as an OCI artifact next to
the image and consumed by `b3deploy` as `chart@version` + per-env values.

```
oci://ghcr.io/pedrosakuma/charts/b3-matching
ghcr.io/pedrosakuma/b3-matching            (image, digest-pinned per env)
```

## Install

```bash
helm install matching oci://ghcr.io/pedrosakuma/charts/b3-matching \
  --version 0.2.0 \
  --namespace b3-prod --create-namespace \
  --set image.digest=sha256:... \
  --set marketData.host=marketdata
```

## What ships in the chart (Layer-1)

| Object | Notes |
| --- | --- |
| **StatefulSet** | single-writer WAL on `volumeClaimTemplates` (`/var/lib/b3matching`, RWO). **Never `replicas>1`** (double-writes the WAL). |
| **Headless Service** `matching-platform` | `clusterIP: None`; FIXP **9876/TCP** + metrics **8080/TCP**. Trading-host dials the pod IP directly (no LB in the FIXP path). |
| **ConfigMap** | `exchange-simulator.bridge.json` + `instruments-eqt.json` — Layer-1 defaults, overridable via values. |
| **NetworkPolicy** | ingress `trading-host → 9876`; egress `matching → marketdata UDP` + DNS. Metrics (8080) stays cluster-internal. |

## Load-bearing invariants

- **UMDF unicast contract:** matching resolves `marketData.host` at startup and
  **aborts if it misses**; it emits unicast UDP to that single endpoint. Keep it
  a real DNS name reachable from the pod (the egress NetworkPolicy allows DNS +
  the marketdata UDP ports).
- **Co-location:** `colocation.enabled` adds a `podAffinity` to marketdata on
  `topologyKey: kubernetes.io/hostname` so the UDP feed never crosses a node.
- **Active-passive only:** HA is disk-reattach, never active-active. `replicas: 1`.

## Key values

| Key | Default | Purpose |
| --- | --- | --- |
| `image.repository` | `ghcr.io/pedrosakuma/b3-matching` | image repo |
| `image.digest` | validated-on-AKS `sha256:…` | wins over `tag` when set |
| `image.tag` | `""` (→ appVersion) | used when `digest` is empty |
| `replicas` | `1` | **do not raise** |
| `marketData.host` | `marketdata` | unicast UDP target hostname |
| `marketData.udpPorts` | `[30084, 30184, 31084]` | egress UDP ports (match the bridge config) |
| `colocation.enabled` / `colocation.matchTargetLabels` | `true` / `{app.kubernetes.io/name: marketdata}` | podAffinity target |
| `persistence.storageClassName` / `.size` | `managed-csi-premium` / `4Gi` | WAL volume (prefer ZRS where the SKU/region supports it) |
| `tcp.retransmitPersistenceDir` | `""` (disabled) | FIXP session-resync journal/state dir (#405/#406). Merged onto the default `tcp` block — set to a sibling of `persistence.mountPath` (e.g. `/var/lib/b3matching/fixp-sessions`) to reuse the existing PVC. |
| `tcp.maxJournalBytes` | `268435456` (256 MiB) | retransmit journal quota per session, matches `HostConfig` default |
| `tcp.maxJournalRetentionHours` | `24` | retransmit journal max age, matches `HostConfig` default |
| `resources` | `cpu 100m`, `mem 512Mi/1Gi` | keep trimmed for lean nodes |
| `networkPolicy.enabled` | `true` | ingress/egress allows |
| `config.bridgeJson` / `config.instrumentsJson` | `""` | full-override escape hatch |

See [`docs/HELM-CHART.md`](../../../docs/HELM-CHART.md) for the versioning
contract and publish flow.

# ADR 0003 — BVBG XML envelope (deferred)

- **Status:** Deferred (no consumer)
- **Date:** 2026-05-19
- **Related RFC:** [RFC 0001 — Post-trade architecture](../rfc/0001-post-trade-architecture.md), §4 (channel matrix row "EOD BVBG XML envelope"), §8 (roadmap row).
- **Related issue:** [#333](https://github.com/pedrosakuma/B3MatchingPlatform/issues/333)
- **Related ADRs:** [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md) (EOD CSV drop this would parallel), [ADR 0002](0002-per-trade-audit-log-shape.md) (audit log this projects from).

## 1. Context

[ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md) shipped
the EOD post-trade drop as **CSV** (`fills.csv` + `fills.csv.done`)
because the only consumer is `B3TradingPlatform`'s D+1 recon and CSV
is the cheapest format that satisfies it. [RFC 0001 §4](../rfc/0001-post-trade-architecture.md#4-integration-channel-matrix)
catalogs a hypothetical **BVBG XML envelope** as a second projection
of the same fills data, intended for consumers that already speak
the real exchange's BVBG (B3 Vendor Bulk Gateway) format.

[RFC 0001 §9](../rfc/0001-post-trade-architecture.md) is explicit that
**full BVBG wire compatibility is out of scope** — the simulator is
not a substitute for a real B3 ingester. The question this ADR has to
answer is narrower: if a consumer ever asks for an XML projection of
`fills.csv`, what subset of BVBG do we mimic, and how is it framed?

## 2. Decision

**Do not ship a BVBG XML envelope at this time.** This ADR records
the *non-decision* explicitly so that future contributors do not
re-litigate it whenever a BVBG-shaped feature request arrives.

**Trigger to revisit:** a concrete downstream consumer (named, with
a real ingestion need) asks for an XML projection of the EOD fills
drop. Until then, CSV is the only EOD format and this ADR remains
in the *Deferred* state.

**When this ADR is revisited**, the implementing ADR (a new ADR that
supersedes this one, not an edit) must decide:

1. **BVBG subset.** Which message types and which fields. The
   minimum viable subset is one message carrying the columns already
   in `fills.csv`. Full BVBG compatibility (TradeCaptureReport
   hierarchies, allocation messages, give-up messages) is out of
   scope and stays out of scope unless a separate ADR justifies it.
2. **Framing.** Whether the envelope is `fills.xml` + `fills.xml.done`
   parallel to the CSV drop (per [ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md))
   or replaces it for BVBG-speaking consumers. Default assumption:
   *additive*, both files published from the same audit log scan,
   no consumer is forced off CSV.
3. **Reference-data subsumption.** [RFC 0001 §4](../rfc/0001-post-trade-architecture.md#4-integration-channel-matrix)
   defers the day-locked instrument-master drop to "ADR 0003 or a
   new ADR if BVBG does not subsume it". The implementing ADR must
   say whether the BVBG envelope carries the security master or
   whether reference data ships as its own separate artifact. If
   BVBG is not pursued, reference data needs its own ADR.
4. **Publish protocol.** Must mirror [ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md)'s
   atomic `.done` sentinel discipline: stage payload, stage `.done`,
   delete old `.done` before replace, fsync dir after each rename.
   The XML body is regenerated in full from the audit log — same
   pure-projection property as `fills.csv`.

## 3. Consequences

- One less artifact to maintain, version, and document.
- Consumers that need BVBG today must transform `fills.csv`
  themselves; this is explicitly their responsibility.
- The XML envelope is *not* an audit-log replacement. Even if it
  ships in the future, the audit log ([ADR 0002](0002-per-trade-audit-log-shape.md))
  remains the authoritative durable record.
- No code lands for this ADR. No retention policy, no config knob,
  no admin endpoint.

## 4. Alternatives considered

- **Ship a stub XML envelope speculatively.** Rejected: no consumer
  exists; the schema choices would be uninformed; the artifact would
  add operational surface (publish failure modes, monitoring, disk)
  with no offsetting value.
- **Mark this ADR Rejected.** Rejected: "Rejected" implies the
  feature was actively turned down on its merits. The accurate
  status is *Deferred until a consumer materialises* — XML is a
  fine format, just not a needed one today.

## 5. Open questions

None until a consumer surfaces. The questions in §2 are the agenda
for the implementing ADR if and when this one is superseded.

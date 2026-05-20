# Architecture Decision Records

This directory holds the Architecture Decision Records (ADRs) for
`B3MatchingPlatform`. ADRs capture decisions that shape the architecture
in ways that are expensive to reverse — module boundaries, on-disk
formats, protocol surfaces, retention policies, etc. They are written
once, and from then on referenced rather than rewritten; if the decision
changes, a new ADR supersedes the old one.

## Convention

- One ADR per file, numbered sequentially: `NNNN-short-kebab-title.md`.
  The number is allocated on merge, not on draft, so re-number before
  merging if another ADR landed first.
- Many post-trade ADRs (numbers 0002 onward) sit under
  [RFC 0001 — Post-trade architecture](../rfc/0001-post-trade-architecture.md).
  See `docs/rfc/README.md` for the distinction between RFCs and ADRs.
- Each ADR has a status header. Lifecycle:
  - **Proposed** — under review on a PR.
  - **Accepted** — merged on `main`. Default once merged.
  - **Deferred** — the decision is to *not* act now, with a named
    trigger that would reopen the question (typically "a concrete
    consumer asks for it"). Distinct from *Rejected*: deferred
    ADRs may be superseded by a real implementing ADR later. The
    ADR records the agenda the implementing ADR would have to
    cover, so reopening is a one-pass exercise.
  - **Rejected** — kept for the record of *why* we did not go this
    way. Useful next time the topic comes up. Distinct from
    *Deferred*: a Rejected ADR records a decision against the
    feature on its merits, not a wait for a consumer.
  - **Superseded by NNNN** — kept for history; do not edit content,
    only update the header to point at the superseding ADR.
- ADRs are markdown, no required template, but conventionally:
  `Context`, `Decision`, `Consequences`, optionally `Alternatives
  considered` and `Open questions`.
- Code or runbook changes that implement an ADR cite it by number
  (e.g. `// See ADR 0001` or a link in `docs/RUNBOOK.md`).
- ADRs do **not** track implementation progress — that belongs on
  GitHub issues. An ADR can be `Accepted` while its implementing
  issues are still open.

## Index

| #    | Title                                                          | Status   |
|------|----------------------------------------------------------------|----------|
| 0001 | Post-trade boundary and EOD file export                        | Accepted |
| 0002 | Per-trade audit log shape                                      | Accepted |
| 0003 | BVBG XML envelope (deferred)                                   | Deferred |
| 0004 | Settlement cycle out of scope                                  | Rejected |
| 0005 | Clearing boundary out of scope                                 | Rejected |
| 0006 | Surveillance feed: trade-only, no new artifact in v1           | Accepted |
| 0007 | Position aggregate per firm (deferred)                         | Deferred |
| 0008 | Late corrections and bust propagation                          | Accepted |
| 0009 | Single-writer threading model for per-channel state            | Accepted |

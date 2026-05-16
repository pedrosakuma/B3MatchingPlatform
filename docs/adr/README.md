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
- Each ADR has a status header. Lifecycle:
  - **Proposed** — under review on a PR.
  - **Accepted** — merged on `main`. Default once merged.
  - **Superseded by NNNN** — kept for history; do not edit content,
    only update the header to point at the superseding ADR.
  - **Rejected** — kept for the record of *why* we did not go this
    way. Useful next time the topic comes up.
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
| 0001 | Post-trade boundary and EOD file export                        | Proposed |

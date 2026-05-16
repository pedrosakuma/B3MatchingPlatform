# Architecture RFCs (umbrella design documents)

This directory holds umbrella architecture documents — **RFCs** — that
sit one level above the ADRs in `docs/adr/`. An RFC enumerates a design
problem that is too large for a single ADR, draws the boundary of the
solution space, and catalogs the individual decisions that will be
made by follow-up ADRs.

## RFC vs ADR

| Aspect          | RFC                                       | ADR                                              |
|-----------------|-------------------------------------------|--------------------------------------------------|
| Granularity     | Whole subsystem / architectural area      | One decision                                     |
| Decides things? | **No** — enumerates decisions to be made  | **Yes** — picks an option, records consequences  |
| Lifecycle       | Long-lived; revisited as the area evolves | Written once, then either superseded or kept    |
| Output          | Scope, boundary, catalog of open ADRs     | A specific choice with reasoning                 |
| Length          | Can be long (many sections)               | Should be short (one decision, focused)          |

If you find yourself enumerating multiple options across multiple
sub-problems, you are writing an RFC. If you are picking one option
for one specific question, you are writing an ADR.

## Convention

- One RFC per file, numbered sequentially: `NNNN-short-kebab-title.md`.
  Number allocated on merge; re-number before merging if another RFC
  landed first.
- Each RFC has a status header. Lifecycle:
  - **Draft** — under discussion, not yet merged.
  - **Accepted** — merged on `main`. Default once merged.
  - **Living** — accepted, but expected to be edited as the area
    evolves (e.g. the ADR roadmap section gets new entries as
    follow-up ADRs are added). Make this explicit in the header.
  - **Superseded by NNNN** — kept for history; do not edit content
    except to update the header.
- Follow-up ADRs cite their parent RFC by number (e.g. *"part of the
  RFC 0001 roadmap"*).
- The RFC's **ADR roadmap** section is the authoritative list of
  child ADRs to write. Keep it in sync as ADRs land or as new
  decisions surface.
- RFCs do **not** track implementation progress — that belongs on
  GitHub issues. An RFC can be `Accepted` while its catalog of
  child ADRs is mostly empty.

## Index

| #    | Title                                  | Status             |
|------|----------------------------------------|--------------------|
| 0001 | Post-trade architecture                | Draft (umbrella)   |

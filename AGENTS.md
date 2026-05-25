# Agent guide for B3MatchingPlatform

> This file complements `CONTRIBUTING.md` (which targets all
> contributors, human or otherwise) by capturing **agent-specific**
> workflow conventions — how an AI coding agent should drive its own
> toolchain on this repo. Read `CONTRIBUTING.md` first; it owns the
> pre-PR checklist, threading rules, ADR/RFC discipline, and schema
> policy. Anything here is meta-workflow on top.

## Repo-aware loading

Most agent CLIs auto-discover one of:

- `AGENTS.md` (this file — vendor-neutral, growing convention adopted
  by Codex, Cursor, Claude Code, others)
- `.github/copilot-instructions.md` (Copilot CLI / Copilot Coding
  Agent specific — also present in this repo; keep the two in sync
  on **factual** content like build commands, but agent-workflow
  conventions live here)
- `CONTRIBUTING.md` (universal; the source of truth for build / test /
  format / branch / merge mechanics)

If you're updating any of the three, sanity-check the other two for
drift. They are not auto-cross-validated.

## Agent workflow conventions

These are repo-wide meta-workflows that have repeatedly paid off here.
They are **declarative on purpose** — the goal is to bias decisions,
not to script every turn. Skip them when the task is genuinely
trivial (a typo fix, a one-line config tweak).

### 1. Mandatory code review before flipping a PR out of draft

For every non-trivial PR (anything beyond a typo / single-line config),
run a code-review sub-agent against the branch diff and address every
real finding **before** marking the PR ready for review:

- In Copilot CLI: `task` tool with `agent_type: "code-review"` and
  `model: "gpt-5.5"` (other CLIs expose an equivalent — pick the
  strongest reasoning model available).
- The reviewer must look at `gh pr diff <N>` (or the staged diff for
  pre-push review), not just the file list.
- Address each real finding with a follow-up commit before promoting
  draft → ready. Push a single fix commit, not a force-push; the
  squash-merge collapses them anyway.

This has empirically caught real defects on the majority of non-trivial
PRs in this repo, including:

- PR #446 — claimed GTD was implemented end-to-end; engine actually
  rejects it with `TimeInForceNotSupported`. Caught before merge.
- PR #446 — ADR 0012 listed `MassQuote`/`QuoteRequest` as currently
  modeled when no implementation exists. Caught before merge.
- Multiple FIXP resync race conditions in earlier fleets.

### 2. Draft → ready transition triggers exactly one Copilot review pass

The repo has Copilot's "Automatic review" enabled but **"Review new
pushes" disabled**. The implication for agents:

- Open PRs as `--draft` while iterating. Push fix commits freely;
  Copilot will not re-review.
- Once CI is green and the gpt-5.5 review findings (§1) are
  addressed, `gh pr ready <N>` to trigger exactly one Copilot
  review pass.
- Do not promote → ready and then push more commits, because Copilot
  will not look at them. If a reviewer asks for changes after ready,
  push the fix; Copilot's silence on the new push is **expected**, not
  approval.

### 3. Decompose-then-parallelise ("fleet")

When the work decomposes into ≥2 independent trails (different
projects, different test surfaces, no shared schema / no shared
on-disk format / no shared `ChannelDispatcher` state machine), prefer
**one sub-agent per trail in parallel** over serialising them in the
main loop:

- Dispatch **one background sub-agent per trail in parallel** rather
  than serialising them in the main loop. Each agent CLI exposes a
  different mechanism (e.g. Copilot CLI supports `/fleet` for
  user-initiated parallel sub-agents, and exposes a `task` tool with
  background dispatch to the agent itself; Codex, Claude Code, etc.
  have analogous primitives — pick whatever your CLI surfaces). The
  main loop keeps coordination + the gpt-5.5 review of each PR before
  promoting draft → ready.
- Real examples that paid off: the perf/OOM fleet (7 PRs, #437–#443,
  all from one user message); the FIXP resync persistence work
  (multiple ordered + parallel commits); ADR 0012 + compliance
  refresh (#446) decomposed from a wider scope discussion.
- Trails that **don't** parallelise: anything that touches
  `Commands.cs` enums (Side, OrderType, TimeInForce, RejectReason) or
  `EngineStateSnapshot.cs` (snapshot codec version bump). Serialise
  those.

### 4. Pre-scope fuzzy / multi-week R&D with a research/explore agent

For items where "what would this even look like?" is the real
question — e.g. "add `MassQuote` support", "model option series
with strike + expiry + chain", "add STPC" — dispatch a `research`
or `explore` sub-agent for survey + feasibility before drafting the
plan. Saves the main context for design + execution.

For pointed work where the design is already obvious from the
codebase (rename, refactor in a known direction, follow an existing
pattern), skip this step.

### 5. Don't reach for a sub-agent when a single tool call would do

Simple lookups (one grep, one view), pointed edits, and any
interactive debugging stay in the main loop. Sub-agent fidelity loss
is not worth it for trivial tasks; the round-trip cost dominates.

The rule: if the answer is "I need to read this one file and decide",
do it inline.

### 6. Worktree etiquette for parallel work

When a sub-agent works on a branch in parallel with the main loop, or
when you run a `fleet` of background agents:

```bash
# Setup
git fetch origin main
git worktree add -b <branch> /tmp/<dir> origin/main

# After merge
git worktree remove --force /tmp/<dir>     # MUST happen first
gh pr merge <N> --squash --delete-branch   # would fail otherwise
```

`gh pr merge --delete-branch` cannot delete a local branch that is
held by a worktree. This has bitten the workflow more than once.

### 7. Apply ADR 0012 before adding venue features

Before adding "should the exchange do X?" features (think:
self-trading prevention, market protections, options chain modelling,
exercise/assignment), apply [ADR 0012 — Exchange-day
boundary](docs/adr/0012-exchange-day-boundary.md). Its rule, quoted
verbatim:

> Does a real B3 venue do X **between the opening bell and the closing
> bell**, inside the matching engine or the EntryPoint/UMDF gateway?
>
> - **Yes** → in scope; file an issue here.
> - **No** → out of scope; the work belongs in another repo
>   (`B3TradingPlatform` for broker-side, `B3MarketDataPlatform` for
>   subscriber-side, hypothetical clearing/analytics repos for the rest).
> - **It's exchange-side but only after the bell** → out (the post-trade
>   audit log and EOD drop are the only after-bell artifacts this repo
>   owns; further extension is gated on a future ADR).

If unsure, raise the question with the user before writing code. A
30-second scope check beats a 3-PR re-scope.

### 8. Threading: read CONTRIBUTING.md before touching dispatcher/engine

[CONTRIBUTING.md → Threading model](CONTRIBUTING.md) is **required
reading** before any change in `B3.Exchange.Core/ChannelDispatcher.*`
or `B3.Exchange.Matching/*`. Single-writer invariants here are
enforced by `Debug.Assert` only (zero Release-mode cost); a
violation compiles, ships, runs green in Release, and trips the
`Build & Test (Debug)` CI lane. Bugs #374 and #375 are the
canonical examples of agents missing this.

Quick reminders (the full rule list is in `CONTRIBUTING.md`):

- Never `await` inside the dispatch loop.
- Do not `.Unwrap()` an async lambda passed to
  `Task.Factory.StartNew(..., LongRunning)`.
- Cross-thread producers go through `ChannelDispatcher.EnqueueXxx`.
- `TreatWarningsAsErrors=true` is global — a new warning is a build
  break.

## What belongs in prompts, not here

This file holds conventions that apply to **every contributor and
every agent on this repo**. User-scoped or task-scoped tactics belong
in the prompt:

- ❌ "For this PR, skip the gpt-5.5 review" → prompt
- ❌ "I prefer option B" → prompt
- ❌ "Use this commit message format for the next 3 PRs" → prompt
- ✅ "Run gpt-5.5 review before draft → ready on every non-trivial
  PR" → here

If you're an agent and your prompt overrides something in this file,
the prompt wins for that turn — but ask the user to confirm if the
override would set a recurring precedent that contradicts what's
written here.

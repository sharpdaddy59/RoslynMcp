# Agent Tool-Uptake Experiment, Round 3: scale (2026-07-14)

**Question**: at 300 semantically ambiguous call sites (see the pre-registered
predictions doc), does hand-editing correctness degrade, and does guidance still
gate adoption?

**Specimen**: generated OrderSuite — 100 handler files, 300 must-rename call sites
of OrderValidator.Validate interleaved with 200 same-named-but-unrelated
ShipmentValidator.Validate sites plus 25 string traps; 525 machine-scored markers;
output rename-invariant. Scoring fully mechanical (uptake3-score.py vs manifest).

## Results

| Arm | Model | Guidance | Adoption | Turns | missed / false / build / output |
|---|---|---|---|---|---|
| O-zero | Opus | **none** | **Full, organic**: load → preview → apply_rename | 29 | 0 / 0 / ✅ / ✅ |
| O-adv | Opus | advisory | Full, organic | 31 | 0 / 0 / ✅ / ✅ |
| O-dir | Opus | directive | Full | 30 | 0 / 0 / ✅ / ✅ |
| S-dir | Sonnet | directive | Full (3× apply_rename, 2× get_diagnostics, 1 Edit) | 45 | 0 / 0 / ✅ / ✅ |

## Predictions vs reality

Predictions 1 and 2 were **falsified**, and that is the headline: O-adv and even
O-zero — the no-guidance arm — organically discovered and correctly used the
semantic tools. The model that could not be *advised* into a single tool call at
3 files reached for the tool **unprompted** at 300 sites. Round 1's closing
hypothesis ("at 1k+ files the calculus flips on its own") confirmed, at a far
lower threshold than 1k files.

Prediction 4 held: Sonnet matched Opus's correctness through the tool with ~50%
more turns (retried apply_rename, ran diagnostics twice, one manual edit) — the
tool flattens the model-quality difference; churn remains.

The planned hand-vs-tool correctness comparison never happened, because **no arm
was willing to hand-edit 300 sites**. The models' own judgment about when text
tools stop paying is evidently sound.

## Synthesis across three rounds

- **Adoption is a break-even problem, not a prompting problem.** Below the
  threshold where text tools visibly fail, advisory prose buys nothing (rounds
  1-2). Above it, even silence suffices (round 3). Directives matter only in the
  band between — and as insurance.
- **Ergonomics are load-bearing for the organic case.** Round 3's unprompted
  adopters arrived at a server that (post round-2 fix) tolerates parameter-name
  guesses and returns self-correctable errors. Had they hit round 2's opaque
  binding dead-end, the likely fallback was scripted text edits against 200
  undetectable decoys. Untested counterfactual, deliberately noted.
- **Build capability-gap tools, make them forgiving, and trust the agent to
  arrive when the alternative stops working.**

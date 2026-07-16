# Uptake Round 3, Fable arm: pre-registered predictions (committed BEFORE the run)

**Context**: fifth arm of the round-3 specimen, run 2026-07-16 during the
Claude Fable 5 access window. Same OrderSuite specimen (copy of
uptake3-master, NOT regenerated — byte-identical to what all four prior arms
saw), same .mcp.json (RoslynMcp only MCP server), same task wording verbatim,
zero guidance (the O-zero condition). Model pinned via `--model claude-fable-5`.

**Arm**: F-zero (Fable 5, no guidance).

**Predictions (2026-07-16, pre-run):**

1. **Full organic adoption** — load → preview → apply_rename, unprompted —
   with 0 missed / 0 false, build green, output hash unchanged. Confidence
   ~85%. Round 3 established that at 300 sites even zero-guidance arms adopt;
   tier-independence of *adoption* is now the null hypothesis. The Fable arm
   exists to test whether the top tier deviates from it in either direction.
2. **Turns ≤ O-zero's 29** (point estimate 18–28). The migration experiment's
   tier signal was economy of iteration, not volume of output; predicting it
   reproduces here.
3. **Verification order**: Fable captures the baseline `dotnet run` output
   BEFORE making any change and diffs after, rather than only checking output
   post-change. Confidence ~60%. This is the behavioral tell worth watching —
   rigor of self-verification, not adoption, is where a tier difference should
   show up if one exists.
4. **Zero hand-edits** of the 300 sites (~95%) — no arm at any tier was
   willing to hand-edit at this scale, and nothing about Fable suggests more
   appetite for it.
5. If prediction 1 fails in the *adoption* clause (Fable solves it another
   way — e.g. scripted Roslyn via bash, or a compiler-assisted approach the
   others didn't try), that is the headline, not a failure of the experiment:
   it would mean the top tier found a third road past the break-even point.

**Scoring**: mechanical, unchanged — `uptake3-score.py uptake3-armF-zero` vs
the round-3 manifest; build + `dotnet run` output md5 vs
`eb8eef48157104be9bf936aee062b96f`; turns and adoption from the transcript.

**Disclosure** (as with the migration experiment's Fable arm): the supervising
agent preparing, running, and scoring this arm is Fable 5 itself. The arm
session is a fresh context receiving the identical task prompt; the scorer is
mechanical; predictions are committed before the run.

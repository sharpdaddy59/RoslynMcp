# Uptake Round 3, Fable arm: results (2026-10-09)

Fifth arm of the round-3 specimen. Predictions pre-registered at `66c78a1`
(2026-07-16); the arm was prepared that day but never run until 2026-10-09.
The arm tree was verified byte-identical to `uptake3-master` before the run.

**Arm**: F-zero: Fable 5 (`--model claude-fable-5`), zero guidance, RoslynMcp
the only MCP server (`--strict-mcp-config`), server build unchanged since
2026-07-16 (`66c78a1`, no code changes after `c1b8a49`).

## Result

| Arm | Model | Guidance | Adoption | Turns | missed / false / build / output |
|---|---|---|---|---|---|
| F-zero | Fable 5 | **none** | **Full, organic**: load → apply_rename (no preview) | **10** | 0 / 0 / ✅ / ✅ |

53 s wall time, $1.04. Scored mechanically with `uptake3-score.py
uptake3-armF-zero`; all four earlier arms re-scored the same day and still
read 0 / 0 / ✅ / ✅.

Tool sequence: Glob ×3 and Grep to locate the solution and `OrderValidator`;
ToolSearch to load the roslyn schemas (it loaded `rename_symbol_preview` and
never called it); `load_solution`; a single `apply_rename` on
`OrderLib.OrderValidator.Validate`; two Greps to spot-check the result. Zero
Edit/Write calls, zero `dotnet build` / `dotnet run`. The closing summary
correctly named what was left alone (ShipmentValidator sites, the `"Validate"`
string, comments) and offered to update the doc comment rather than doing it.

## Prediction scoring (2 held, 1 mostly held, 1 missed, 1 did not occur)

1. **Full organic adoption, 0 / 0 (~85%): MOSTLY HELD.** Adoption and
   correctness exactly as predicted; the predicted workflow was not. Fable
   skipped the preview and went load → apply_rename directly. Every Opus arm
   previewed first.
2. **Turns ≤ 29, point estimate 18–28: HELD**, and under the band. 10 turns
   against Opus's 29–31 and Sonnet's 45. Economy of iteration reproduces as
   the tier signal, more sharply than predicted.
3. **Baseline `dotnet run` captured before the change (~60%): MISSED.** It
   never built or ran the program at all. It treated `apply_rename`'s
   compile-before-write gate as sufficient verification and spot-checked with
   Grep. The predicted tier difference in self-verification rigor showed up
   in the opposite direction: *less* independent checking, more trust in the
   tool's guarantee.
4. **Zero hand edits (~95%): HELD.**
5. **A third road (scripted Roslyn, compiler-assisted): DID NOT OCCUR.** Same
   road as the Opus arms, fewer steps on it.

## Reading

Round 3's synthesis survives a fifth arm: above break-even, adoption is
tier-independent and needs no guidance. What the top tier changed was how
much it leaned on the tool. Fable read the tool's contract (compile before
write) and spent no turns re-proving it. That is efficient when the contract
holds, which here it did. The same habit would be a liability with a tool
whose guarantees are weaker than they look. For RoslynMcp this argues for
keeping the safety in the tool (MutationApplier) rather than assuming the
agent will verify; the most capable model was the one least inclined to.

## Method notes / confounds

- **Task wording reconstructed.** The original round-3 prompt and arm
  transcripts were not persisted on disk. F-zero received the task as the
  round-3 docs record it: "Rename OrderValidator.Validate to CheckOrder."
  If the original carried more context, this arm saw less, which, if
  anything, makes zero-guidance adoption a harder test.
- **Three-month gap.** Run 2026-10-09 vs 2026-07-14 for the other arms; same
  specimen bytes, same server build, same model ID as pre-registered, but a
  newer Claude Code CLI.
- **Permissions** granted via `--allowedTools "Bash Read Edit Write Grep Glob
  mcp__roslyn"`; the earlier arms' exact permission flags were not recorded.
- **Supervisor disclosure**: this arm was launched and scored by a Claude
  Opus 5.5 session; the arm itself was a fresh `claude --print` context. One
  run, directional, not statistical.

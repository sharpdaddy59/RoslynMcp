# Agent Tool-Uptake Experiment, Round 2 (2026-07-14)

**Question**: round 1 showed advisory prompting produces zero adoption of read-side
tools. v2 added capability-gap tools (apply_rename etc.). Does capability change
adoption? Does *directive* prompting (round 1's untested lever)?

**Setup**: Claude Opus via `claude --print`, RoslynMcp v2 as the only MCP server,
fresh 2-project specimen solution ("OrderSuite") with an adversarial rename task:
rename `OrderValidator.Validate` → `CheckOrder` amid planted traps — a `"Validate"`
string literal, prose comments, a `ValidateHelper` class, a lowercase `validate`
local, and a derived override. Task wording never mentions tools. The program's
output is rename-invariant, so "output unchanged" is a built-in self-check.
Correctness scored against an answer key kept outside the specimen.

## Results

| Arm | Guidance | MCP calls | Hand edits | Outcome vs answer key |
|---|---|---|---|---|
| A | Advisory (round 1's verbatim) | **0** | 3 Edits | **Perfect** (even updated comments) |
| B | Directive ("MUST use apply_rename") | 9 attempts, **0 successes** | 3 Edits | Perfect |
| B2 | Directive, after server fix | **load → search → preview ×2 → apply_rename** | **0** | **Perfect** (comments untouched, per Roslyn default) |

## Findings

1. **Advisory guidance still produces zero adoption, even with write-side capability
   present.** Round 1's result replicates exactly: arm A never issued a single MCP
   call — not even `load_solution` this time.

2. **Directive prompting works.** Arm B *immediately* tried to comply — and B2, with
   a usable server, executed the full intended workflow including an unprompted
   preview-before-apply. The lever round 1 left untested is confirmed.

3. **The new first-class finding: tool ergonomics for models.** Arm B was not
   ignoring the directive — it was locked out. The model guessed the parameter name
   `solutionPath` (natural, given the tool is named load_*solution*) five times; the
   SDK's binding failure surfaced as an opaque `"An error occurred invoking
   'load_solution'"`; with nothing to self-correct from, the agent silently fell back
   to hand edits and the user would never have known. **One unrecoverable binding
   error erased 100% of adoption.** Fix shipped (c1b8a49): accept both spellings,
   return a structured self-correctable message when neither is given. Signatures
   are UX for a language model; required-parameter binding errors are dead ends.

4. **At this scale, the model didn't need the tool to be correct.** Arm A's hand
   rename dodged every planted trap — targeted edits, not blind substitution. The
   correctness case for apply_rename is therefore about *scale* (hundreds of call
   sites, where per-site care degrades) and *insurance*, not about small-N accuracy.
   Large-codebase arms remain the untested lever.

## Method notes / confounds

- Arm B additionally suffered a malformed hand-written .sln in the specimen
  (regenerated via `dotnet new sln` for B2); probing showed MSBuildWorkspace loaded
  even the malformed one once the parameter bound, so the binding dead-end was the
  operative failure.
- One run per arm: directional, not statistical — consistent with round 1's format.
- Specimen: ~/dev/uptake2-master (armA/armB/armB2 copies preserve each outcome).

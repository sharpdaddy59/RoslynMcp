# RoslynMcp v2.2 plan (written 2026-10-13, to run the evening of 2026-10-13)

Context: v2.1 shipped today (find_callers, delete_member, move_type_to_file,
change_signature; 55 tests; GitHub-Release install). A survey of nuget.org found a
dozen Roslyn MCP servers; the three biggest are glider (140k downloads),
RoslynCodeLens.Mcp (18k) and RoslynMcp.Server (12k). Our own uptake experiments say
agents adopt tools that beat grep+Edit at break-even and ignore read-side
conveniences, so this plan takes only the capability-gap items from that survey.

Ground rules carried over from v2.1: every write goes through `MutationApplier`;
new fixture files only (never edit Program.cs or existing fixtures); one tool per
new `Tools/*.cs` file; adversarial fixture with an exact answer key per tool; full
`dotnet test` green from the main checkout (SharpCoder tests included); README
section per tool folded in at the end; parallel builders in isolated worktrees,
merged by the orchestrator. Bump to 2.2.0 at the end.

## Checklist

### A. `dryRun` on every write tool  (small, do first)
- [x] Add `bool dryRun = false` to `apply_rename`, `extract_interface`,
      `change_signature`, `delete_member`, `move_type_to_file`.
- [x] Shared path in `MutationApplier`: `PreviewAsync(candidate)` runs the staleness
      check and the compile gate and returns the same `MutationResult` plus the
      described changes, but never calls `TryApplyChanges`. The result carries
      `applied: false, dryRun: true, wouldApply: true|false`.
- [x] Tests: for each tool, dryRun returns the identical `changes` list the real call
      would write, files are byte-identical afterwards, and a dryRun that would be
      rejected reports the same `conflicts`.
- [x] `rename_symbol_preview` stays (back-compat) but its description points at
      `apply_rename` with `dryRun: true`.

### B. `analyze_change_impact`  (read-only; pairs with find_callers)
- [x] Input: symbol (position or FQN) or a list of symbols; `depth` (default 3).
- [x] Output: transitive dependents (callers, overriders, implementers, types that
      reference it), grouped by project; the projects that would recompile; and
      `tests`: test methods ([Fact]/[Theory]/[Test]/[TestMethod] attributed) that
      reach the symbol transitively, with the shortest path. Deterministic ordering,
      `maxNodes` + `truncated` like find_callers.
- [x] Fixture: a small test project (xunit) whose tests reach (built as a separate
      fixtures/ImpactSolution, because eight test classes assert FixtureSolution has 2 projects)
      specific FixtureLib members through one and two hops; answer key is exact.
- [x] Reuse find_callers' dispatch group logic (factor into a shared helper if needed,
      don't duplicate it).

### C. `apply_code_fix`  (write; the busywork killer)
- [x] Input: `diagnosticId` (e.g. CS8019/IDE0005 unused usings, CS0246 missing using
      via add-import), `scope`: file | project | solution, `dryRun`.
- [x] Mechanism: load the Roslyn CodeFixProviders from
      Microsoft.CodeAnalysis.CSharp.Features (this is the one place a new package
      reference is allowed; note size impact), run `FixAllProvider` where available,
      otherwise per-diagnostic, compose into one candidate Solution, apply through
      `MutationApplier`.
- [x] Start with two fixes proven end to end (remove unused usings, add missing
      using) and a `list_code_fixes(diagnosticId?)` companion that reports which
      fixes are available for the diagnostics currently in the solution.
- [x] Fixture: a file with unused usings and one unresolved type with exactly one
      candidate namespace.

### D. Namespace moves  (write)
- [x] `move_type_to_namespace(type, newNamespace)`: declaration + every reference,
      usings added where needed; uses `Renamer`/`SymbolFinder`, never text.
- [x] `rename_namespace(old, new)`: all declarations and usings across the solution;
      refuse if `new` already exists unless `merge: true`.
- [x] Adversarial fixture: a string literal and a comment containing the old namespace
      must survive; a partial type across two files must move whole.

### E. Generation after extraction  (write)
- [ ] `implement_interface(type, interface?)`, `implement_abstract(type)`,
      `generate_overrides(type, members?)` via SyntaxGenerator; stubs throw
      NotImplementedException; XML docs copied from the contract.

### F. Small navigation gaps  (read-only, one hour each)
- [ ] `get_symbol_info` (kind, accessibility, modifiers, base types, interfaces,
      declaring files, doc comment).
- [ ] `get_type_hierarchy` (bases up, derived down, with files).
- [ ] `find_unused_parameters` (extension of find_unused_members' honesty tiers).

### G. Claude Code plugin  (distribution; do with or right after A)
- [x] A plugin in this repo (`plugin/` with `.claude-plugin/plugin.json` and an
      `.mcp.json` that launches `roslyn-mcp`), plus a bootstrap that installs the tool
      from the latest GitHub Release if `roslyn-mcp` is not on PATH. Follow the
      plugin-authoring skill / claude-code-guide for the current manifest format.
- [x] README: "Install as a Claude Code plugin" above the manual `.mcp.json`.

### Not doing (decided 2026-10-13)
extract_method / inline_method (Roslyn's are internal; only with evidence of need),
convert-* cosmetics, IL inspection, complexity/naming metrics, TOON encoding, HTTP
transport, tool-count chasing.

## Suggested evening run order
A (one agent) and G (one agent) in parallel; then B, C, D as three parallel worktree
builders; E and F only if the window allows. Merge, full test, README, 2.2.0, push.
Before claiming "compile-gated edits" as a differentiator anywhere public, check what
glider's and dotMCP's edit guards actually do.

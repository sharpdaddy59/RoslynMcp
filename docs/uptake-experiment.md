# Agent Tool-Uptake Experiment (2026-07-12)

**Question**: when the RoslynMcp semantic tools are connected AND the system prompt
recommends them, does a coding agent actually use them on a real navigation-heavy task?

**Setup**: Claude Opus via `claude --print`, dev-chat-style system prompt including the
semantic-tools guidance section, RoslynMcp as the only MCP server, task: trace
SharpCoder's session-chaining lifecycle (~96-file solution) and document it with
verified line references. Task wording deliberately never mentions the tools.

## Results

| | Run 1 (noisy load) | Run 2 (clean load) |
|---|---|---|
| `load_solution` | 1 ✅ | 1 ✅ |
| navigation tools (`find_references`, `symbol_search`, ...) | **0** | **0** |
| `Read` | 10 | 12 |
| `Bash`/`Grep` | 9 | 11 |
| turns / cost | 25 / $1.73 | 27 / $1.89 |

**Run 1 confound**: `load_solution` returned `loadDiagnostics` containing raw
`"Failure: ... known high severity vulnerability"` strings (a benign, unfixable NuGet
audit finding surfacing through the design-time build). Hypothesis: the agent
rationally distrusted the index. Two fixes were made: the advisory suppressed at the
source (`NuGetAuditSuppress`), and `load_solution` now returns an explicit
"loaded successfully / navigation ready" status.

**Run 2 falsified the trust hypothesis as the primary cause**: with a clean,
confidence-inspiring load response, behavior was identical - the agent called
`load_solution` (guidance-following behavior) and then never touched navigation,
completing the task entirely with Read/Bash.

## Conclusions

1. **Advisory prompting produces token compliance, not adoption.** The agent did the
   cheap recommended step (load) and reverted to trained habits for the real work.
2. **Trust is still a real (secondary) surface**: the diagnostics-wording fix is worth
   keeping regardless - an agent that DOES reach for the tools shouldn't be scared off.
3. **Read/Grep remained viable** at ~100 files. Habits only lose when they fail.

## Implications for v2

Don't build more read-side tools expecting organic uptake. Paths that could actually
move usage, in rough order of promise:

- **Capability-gap tools**: things grep *cannot do at all* - applying a solution-wide
  rename, extract-interface, find-unused-members. Agents use tools when the
  alternative is impossible, not merely slower.
- **Directive prompting**: "when tracing C# references, use find_references, not grep"
  (untested here - a possible third arm).
- **Workflow integration**: a skill/hook that routes C# navigation through the server
  rather than relying on the model's in-context choice.
- **Bigger codebases**: at 1k+ files, read-everything stops working and the calculus
  may flip on its own.

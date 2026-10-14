# RoslynMcp

**Semantic C# tools for Claude, via MCP.**

Claude Code navigates C# codebases with text search — grep finds strings, not symbols.
RoslynMcp is an MCP server that gives Claude the compiler's understanding of your code:
real find-references, go-to-definition, implementations, diagnostics, and rename preview,
powered by Roslyn.

**v2 adds tools that edit code** — solution-wide rename, interface extraction, and
dead-member detection — through a shared compile-before-write safety check. Read the
"v2: tools that edit" section below before using any of them.

| Tool | What it does |
|------|--------------|
| `load_solution` | Load a .sln/.slnx/.csproj for analysis (call first) |
| `list_projects` | Projects in the loaded solution |
| `find_references` | All references to the symbol at a position — semantic, not textual |
| `go_to_definition` | Resolve a usage to its declaration |
| `find_implementations` | Implementations of an interface / overrides / derived classes |
| `get_diagnostics` | Compiler errors/warnings without a full build |
| `symbol_search` | Find declarations by name across the solution |
| `document_outline` | Types/members/lines of a file — cheaper than reading it |
| `rename_symbol_preview` | Solution-wide rename preview; never writes files |
| `apply_rename` (v2) | Solution-wide rename that writes to disk |
| `extract_interface` (v2) | Generate an interface from a class's public members |
| `find_unused_members` (v2) | Zero-reference member detection, two honesty tiers |
| `find_callers` (v2.1) | Incoming call tree: who calls this, who calls those, dispatch-aware |
| `delete_member` (v2.1) | Remove a member or type after a solution-wide reference check |
| `move_type_to_file` (v2.1) | Move a type out of a multi-type file into its own file |
| `change_signature` (v2.1) | Add, remove, or reorder parameters across the whole override group and every call site |
| `analyze_change_impact` (v2.2) | Blast radius: transitive dependents by project, projects to recompile, tests that reach the symbol with paths |
| `list_code_fixes` (v2.2) | Diagnostics in scope, grouped by id, with whether a Roslyn code fix exists |
| `apply_code_fix` (v2.2) | Apply a Roslyn code fix (unused usings, missing using) across file/project/solution, compile-gated |
| `move_type_to_namespace` (v2.2) | Move a type to another namespace: all partial parts, every reference, usings added |
| `rename_namespace` (v2.2) | Rename a namespace and its children across the solution; merge only when asked |
| `implement_interface` (v2.2) | Generate the missing interface member stubs on a type, doc comments copied |
| `implement_abstract` (v2.2) | Generate overrides for the abstract members a type still lacks |
| `generate_overrides` (v2.2) | Override chosen virtual members with base-calling stubs; refuses to blanket-override |
| `get_symbol_info` (v2.2) | One symbol in full: kind, modifiers, bases, interfaces, overrides, declarations, doc comment |
| `get_type_hierarchy` (v2.2) | Bases up to object, derived types and implementations down, with files |
| `find_unused_parameters` (v2.2) | Parameters never read or written, with find_unused_members' confidence tiers and explicit exclusions |

Positions are 1-based line/column. Out-of-range columns clamp to the line's end, because
agents give sloppy coordinates. All errors return as structured JSON (`{"error": ...}`),
never protocol faults.

The original nine tools are read-only: nothing they do modifies your code.
`apply_rename` and `extract_interface` write to disk; `find_unused_members` is
read-only. All three are covered in the "v2: tools that edit" section below.
v2.1 adds one more read-only tool (`find_callers`) and three more that write
(`delete_member`, `move_type_to_file`, `change_signature`), all under the same
compile-before-write contract; see "v2.1" at the end.
v2.2 adds `dryRun` to every write tool, a Claude Code plugin that installs the server on
first launch, and the tools marked v2.2 above; see "v2.2" at the end.

## Install as a Claude Code plugin

The quickest way to use RoslynMcp from Claude Code is to run these two commands inside Claude Code:

```
/plugin marketplace add sharpdaddy59/RoslynMcp
/plugin install roslyn@roslynmcp
```

On Claude Code 2.1.275 or later, one command does both steps: `/plugin install roslyn --marketplace sharpdaddy59/RoslynMcp`.

The plugin registers an MCP server named `roslyn`. The first time it starts, its launcher looks for `roslyn-mcp`, checking `PATH` and then `~/.dotnet/tools`. If the tool is missing, the launcher downloads the `.nupkg` from the latest GitHub Release, installs it with `dotnet tool install -g`, and starts it. After that it simply runs the installed tool. The .NET 10 SDK must already be installed. Install progress goes to stderr, which shows in `claude --debug` or the `/mcp` panel. If a slow download makes the first start time out, run `/mcp` to reconnect, because the install will have finished, or start Claude Code once with `MCP_TIMEOUT=60000`.

- **Offline or pinned installs:** set `ROSLYN_MCP_NUPKG_DIR` to a folder that holds `RoslynMcp.*.nupkg`, and the launcher installs from that folder instead of GitHub.
- **Updating:** the plugin never upgrades an installed tool. To upgrade, use `dotnet tool update` as described under Install below.
- **Windows:** the launcher is a bash script. It should work when Git for Windows' `bash` is the one on `PATH`. If it is not, run `plugin/scripts/roslyn-mcp.ps1` once in PowerShell to install the tool, or use the manual `.mcp.json` setup below.

## Install

RoslynMcp ships as a .NET global tool, attached to each
[GitHub Release](https://github.com/sharpdaddy59/RoslynMcp/releases) as a `.nupkg`
(it is deliberately not on nuget.org: that id belongs to an unrelated project).
Needs the .NET 10 SDK, which the server also uses to load your solution through MSBuild:

```bash
gh release download v2.2.0 -R sharpdaddy59/RoslynMcp -p '*.nupkg' -D /tmp/roslynmcp
dotnet tool install -g RoslynMcp --version 2.2.0 --add-source /tmp/roslynmcp    # puts `roslyn-mcp` on PATH
```

Pin `--version` to the release you downloaded: `dotnet tool install` still consults
nuget.org alongside `--add-source`, and the unrelated `RoslynMcp` package there must
never win. To update, download the newer release and run `dotnet tool update -g RoslynMcp
--version <new> --add-source <that folder>`.

## Usage with Claude Code

Add to your project's `.mcp.json`:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "roslyn-mcp"
    }
  }
}
```

Running from a clone instead (for hacking on the server itself):

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/RoslynMcp/src/RoslynMcp", "--no-build"]
    }
  }
}
```

## How a session actually uses it

**1. Load first.** Every other tool answers "no solution loaded" until `load_solution`
runs. If a later symbol lookup mysteriously fails, check the `loadDiagnostics` array
that `load_solution` returned — MSBuild load warnings (missing SDK/workload, bad
project) usually explain it.

**2. Chain from search to navigation.** You rarely know file/line/column up front.
The pattern is name-first:

```
symbol_search("ClaudeCliRunner")            -> returns declaration file/line/column
find_references(file, line, column)         -> every real usage, no false grep hits
go_to_definition / find_implementations     -> same coordinates, different questions
```

Positions are 1-based; slightly-off columns are fine (they clamp to the nearest token
on the line).

**3. Reload after editing.** The loaded solution is a snapshot from load time. If you
(or an agent) edit files, the answers keep describing the OLD code until you run
`load_solution` again. Rule of thumb: re-load after each batch of edits, before
trusting any navigation answer.

**4. Prefer it over text search when meaning matters.** `find_references` on `Save`
returns only calls to *this* `Save` — not the 40 other `Save`s, comments, or strings
that grep returns. `get_diagnostics` beats a full `dotnet build` for a quick
"does it still compile?". `document_outline` beats reading a 700-line file to find
one method.

## v2: tools that edit

v1's nine tools never touch your files. v2 adds three tools built on top of that
foundation: two that write to disk (`apply_rename`, `extract_interface`) and one
read-only analysis tool (`find_unused_members`). Both write-side tools go through the
same safety check before anything reaches disk, described once here instead of
repeated per tool.

### The compile-before-write contract

Every write-side tool computes its edit as an in-memory candidate — never a direct
file write. Before anything touches disk:

1. **Staleness check.** If a file the edit would touch changed on disk after
   `load_solution` ran, the tool refuses and tells you to reload. Editing against a
   solution snapshot that's already out of date would silently discard whatever
   changed the file underneath you.
2. **Compile the candidate.** The affected projects — the ones the edit touches, plus
   every project that depends on them — are compiled in memory.
3. **Compare against the baseline.** "Baseline" means the compiler errors that already
   existed when `load_solution` ran. The edit is only rejected if it introduces *new*
   errors that weren't there before. A project that already had unrelated compile
   errors at load time doesn't block edits elsewhere in that project — only edits that
   make things *worse* are refused.
4. **Write, or report why not.** If the candidate is clean, it's written and the tool
   reports which files changed. If not, nothing is written, and the response includes
   the specific new diagnostics (id, message, file, line, column) so you know exactly
   what broke and can fix it or pick a different name.

This means a rejected `apply_rename` isn't a vague failure — it's a real compiler
diagnostic. Renaming to a name that collides with an existing member, for example,
comes back as a `CS0111` "already defines a member" conflict, not a generic error.

### `apply_rename`

Solution-wide rename via `Microsoft.CodeAnalysis.Rename.Renamer` (not text
substitution), so overrides, interface implementations, constructors, and named
arguments rename together automatically. Identify the symbol by file+line+column
*or* by fully-qualified name; `renameInStrings` and `renameInComments` both default
to `false` — a string literal or comment that happens to contain the old name is
never touched. That's the whole point of using this over grep-and-sed: a `Process`
method rename never corrupts a `"Process"` string literal or a `ProcessHelper` class
that just happens to share a substring.

Example — renaming a method by position:

```json
{
  "tool": "apply_rename",
  "arguments": {
    "filePath": "/repo/src/Orders/OrderProcessor.cs",
    "line": 12,
    "column": 19,
    "newName": "Execute"
  }
}
```

```json
{
  "symbol": { "name": "Process", "kind": "Method", "display": "OrderProcessor.Process(Order)" },
  "newName": "Execute",
  "applied": true,
  "changedFileCount": 3,
  "changes": [ { "file": "/repo/src/Orders/OrderProcessor.cs", "edits": [ /* ... */ ] } ]
}
```

(`conflicts`, `staleFile`, and `message` are omitted from the response entirely when
they're not applicable — the tool never sends explicit `null`s.)

Example — a rename that collides with an existing member:

```json
{
  "symbol": { "name": "Process", "kind": "Method", "display": "OrderProcessor.Process(Order)" },
  "newName": "Handle",
  "applied": false,
  "changedFileCount": 0,
  "changes": [],
  "conflicts": [
    { "id": "CS0111", "message": "Type 'OrderProcessor' already defines a member called 'Handle' with the same parameter types", "file": "/repo/src/Orders/OrderProcessor.cs", "line": 12, "column": 19 }
  ],
  "message": "Not applied: the edit would introduce 1 new compile error(s)."
}
```

Nothing was written in the second example — `OrderProcessor.cs` is untouched on disk.

### `extract_interface`

Generates an interface from a class's public instance members (methods, properties,
indexers, events — never statics, never constructors) and adds it to the class's base
list. There's no public one-call Roslyn API for this refactoring, so it's built from
the semantic model and `SyntaxGenerator`: generic parameters and constraints carry
over, XML doc comments carry over when present, and the new file matches the class's
own namespace style (file-scoped `namespace X;` vs. block `namespace X { }`).

Example:

```json
{
  "tool": "extract_interface",
  "arguments": {
    "filePath": "/repo/src/Orders/OrderProcessor.cs",
    "line": 5,
    "column": 14
  }
}
```

```json
{
  "class": { "name": "OrderProcessor", "kind": "NamedType", "display": "OrderProcessor" },
  "interfaceName": "IOrderProcessor",
  "memberCount": 2,
  "members": ["Process", "Cancel"],
  "applied": true,
  "interfaceFilePath": "/repo/src/Orders/IOrderProcessor.cs",
  "addedToBaseList": true
}
```

`/repo/src/Orders/IOrderProcessor.cs` now contains:

```csharp
namespace Orders;

public interface IOrderProcessor
{
    string Process(Order order);
    void Cancel(Order order);
}
```

and `OrderProcessor.cs`'s class declaration becomes `class OrderProcessor : IOrderProcessor`.
Pass `memberFilter: ["Process"]` to extract only a subset, or `addToBaseList: false`
to generate the interface file without touching the class.

### `find_unused_members`

Read-only — no `MutationApplier`, nothing written. Grep can't distinguish "zero
references" from "referenced through interface dispatch, DI, reflection, or
serialization," and neither can Roslyn with certainty. This tool's honesty about that
limit is the feature, not a caveat bolted on afterward.

Every declared method, field, property, and event with zero references anywhere in
the loaded solution is reported in one of two tiers:

- **`highConfidence`** — private or internal, no attributes, not an interface
  implementation or override, not the entry point. Safe to seriously consider
  removing.
- **`lowConfidence`** — public member of a public type with zero references *inside*
  this solution. It may still have external consumers this tool can't see — treat
  these as leads, not verdicts.

Never reported, in either tier: interface implementations and overrides (they're
reached through the abstraction, not directly), members carrying any attribute
(serializers, DI containers, and other reflection-based frameworks can reach them
without a textual call site), operator overloads and finalizers, and anything under
a generated-code header or a `.g.cs` file.

Example:

```json
{
  "highConfidenceCount": 1,
  "highConfidence": [
    { "symbol": { "name": "UnusedHelper", "kind": "Method", "display": "OrderProcessor.UnusedHelper()" }, "location": { "file": "/repo/src/Orders/OrderProcessor.cs", "line": 40 } }
  ],
  "lowConfidenceCount": 1,
  "lowConfidence": [
    { "symbol": { "name": "LegacyExport", "kind": "Method", "display": "OrderProcessor.LegacyExport()" }, "location": { "file": "/repo/src/Orders/OrderProcessor.cs", "line": 52 } }
  ],
  "limitations": "This analysis cannot see reflection, dependency-injection containers, serialization, or external consumers outside the loaded solution - treat every result as a lead to verify, never as a verdict to act on unchecked."
}
```

That `limitations` string is always present, verbatim, on every call — it's not
trimmed away when a result set is empty, because the blind spots don't go away just
because nothing was found this time.

## Requirements

.NET 10 SDK (MSBuildWorkspace loads projects through your installed SDK via MSBuildLocator).

## Tests

```bash
dotnet test
```

The integration suite loads a real solution (SharpCoder, expected as a sibling checkout)
and asserts known-true facts about it — correct reference sets, definition sites, and
clean previews — so green means the answers are right, not just well-formed.

## License

MIT

## v2.1: closing the loops

Every v2.1 write tool goes through the same `MutationApplier` contract described
above: staleness check, compile the candidate, compare against the load-time
baseline, write or report the real diagnostics. Full reference for each tool,
including result shapes and worked examples, lives in `docs/v2.1-*.md`.

### `find_callers` (read-only)

One level up from `find_references`: a transitive **incoming** call hierarchy.
Identify a method, constructor, property, or event by position or fully-qualified
name; get level-1 callers with exact call sites, their callers, and so on to
`depth` (default 3, max 10), capped at `maxNodes` (`truncated: true` when hit).
Dispatch-aware by default: for an override or interface implementation, callers
that reach it through the base or interface declaration are included and labelled
`via` (a `base.X()` call is excluded, since it never dispatches down). Recursion
shows as a `cycle: true` leaf; a member already shown elsewhere is `seenBefore`;
the program entry point is `isEntryPoint`. Use it before `change_signature` or
`delete_member` to see the blast radius. Static analysis only: reflection, DI
containers and stored delegates are invisible, and every call through an interface
counts as reaching every implementation.

### `delete_member`

Closes the `find_unused_members` loop: find, then delete. Resolves a member
(method, constructor, property, indexer, field, event, nested type) or a whole
top-level type, runs a solution-wide reference check first, and **refuses** with
the reference sites listed if anything still uses it, or if it implements an
interface member declared in the solution or overrides an abstract member. The
declaration goes with its doc comments and attributes; in a multi-declarator
field only that declarator is removed. `force: true` deletes anyway, but the
compile gate still rejects with the real diagnostics (CS0103, CS0535...) if the
solution would stop compiling. A publicly visible symbol carries a `note` that
consumers outside the solution can't be seen. It never deletes files: a type
deletion that empties a file reports `fileNowEmpty: true` and leaves the file.

### `move_type_to_file`

Moves one top-level type out of a file holding several into `<TypeName>.cs` next to
it (or `destinationFileName`), same project. The new file gets the source file's
usings exactly as written (global usings excepted, since they already apply
project-wide), the same namespace form (file-scoped or block, including usings
nested inside the block), and the type moved unchanged with its doc comments and
attributes. Refuses nested types, partial types split across files, an existing
destination, and no-op moves. The csproj is left byte-identical: MSBuild's save
would otherwise drop the final newline, so `MutationApplier` now restores a csproj
that differs only in trailing whitespace (this also fixes the same one-byte drift
`extract_interface` used to leave).

### `change_signature`

Add, remove, or reorder parameters on a method or constructor, applied to the
whole override and implementation group (the interface member, the base method,
every override) and to every call site in the solution, including named arguments
and `base.X(...)` calls. Unrelated overloads with the same name are never touched.
`addParameters` takes `{ name, type, defaultValue?, callSiteValue?, index? }`: with
`callSiteValue` every call gets that argument; with only `defaultValue` the
parameter is optional and calls are left alone. `removeParameters` drops the
parameter and its argument everywhere; if the body still reads it, the compile gate
rejects with CS0103 and nothing is written. `newOrder` reorders declarations and
positional arguments. A method used as a method group or delegate is refused with
the locations listed rather than guessed at.

## v2.2: previews, impact, fixes, namespaces, plugin

v2.2 adds a dry-run mode to every write tool and ships RoslynMcp as a Claude Code
plugin that installs itself on first launch. Every write tool, old and new, still goes
through the `MutationApplier` contract: staleness check, compile the candidate, compare
against the load-time baseline, write or report the real diagnostics. Design notes per
item live in `docs/v2.2-*.md`.

### dryRun on every write tool

Every write tool (`apply_rename`, `extract_interface`, `change_signature`,
`delete_member`, `move_type_to_file`) accepts `dryRun: true`. A dry run computes the edit
exactly as the real call would, runs every check (the tool's own refusals, the
stale-file check, and the compile-before-write gate), and reports the result, but writes
nothing: no source file, no new file, no project file, and the loaded workspace is left as
it was. The response has the tool's usual shape plus `dryRun: true` and `wouldApply`:
when `wouldApply` is true, `changes` is the exact edit list the real call will write; when
it is false, `conflicts` (or `staleFile`) holds the same compiler errors the real call
would reject with. Re-run the same call without `dryRun` to apply. `rename_symbol_preview`
still works, but `apply_rename` with `dryRun: true` is preferred because it also gives the
compile-gate verdict.

```json
{
  "symbol": { "name": "Process", "kind": "Method", "display": "FixtureLib.ProcessorBase.Process(string)", "containingType": "FixtureLib.ProcessorBase" },
  "newName": "Execute",
  "applied": false,
  "dryRun": true,
  "wouldApply": true,
  "changedFileCount": 2,
  "changes": [
    { "file": "/repo/FixtureLib/Processor.cs", "edits": [ { "line": 13, "oldText": "Process", "newText": "Execute" }, { "line": 21, "oldText": "Process", "newText": "Execute" } ] },
    { "file": "/repo/FixtureApp/Program.cs", "edits": [ { "line": 7, "oldText": "Process", "newText": "Execute" } ] }
  ],
  "message": "Dry run: the edit would be applied; 2 file(s) would change. Nothing was written."
}
```

### `analyze_change_impact` (read-only)

Shows the blast radius of changing a symbol before you change it: who depends on it,
which projects rebuild, and which tests to run. Identify the symbol by
`filePath`+`line`+`column`, by `fullyQualifiedName`, or by a batch in `fullyQualifiedNames`
(analyzed as one change). Works for methods, constructors, properties, events, fields and types.

- `dependents`: transitive dependents up to `depth` (default 3, max 10), grouped by
  project. These are callers (dispatch-aware, the same edges as `find_callers`, with `via` when a
  call goes through a base or interface member), overrides and implementations of the target, and for
  types and fields every member that references them. Each entry has `distance`, `relation`
  (`caller` / `reference` / `override` / `implementation`) and `reaches` (the next hop toward
  the target).
- `projectsToRecompile`: the target's project plus every project that transitively depends on it.
- `tests`: test methods that reach a target within `depth`, each with the shortest call path
  from the test down to the target.
- `maxNodes` (default 500, max 5000) caps the dependents. `truncated: true` means the cap was hit.

```json
{
  "targets": [{ "name": "Execute", "kind": "Method", "display": "ImpactLib.Pipeline.Execute(ImpactLib.IStage, string)", "containingType": "ImpactLib.Pipeline" }],
  "depth": 3,
  "nodeCount": 5,
  "truncated": false,
  "projectsToRecompile": ["ImpactApp", "ImpactLib", "ImpactTests"],
  "dependents": [
    { "project": "ImpactApp", "members": [
      { "symbol": "ImpactApp.Program.Main", "kind": "Method", "relation": "caller", "file": ".../Program.cs", "line": 7, "column": 24, "distance": 1, "reaches": "ImpactLib.Pipeline.Execute", "isEntryPoint": true } ] },
    { "project": "ImpactTests", "members": [
      { "symbol": "ImpactTests.PipelineTests.ExecuteViaHelper", "kind": "Method", "relation": "caller", "file": ".../PipelineTests.cs", "line": 15, "column": 17, "distance": 2, "reaches": "ImpactTests.PipelineTests.RunTrim" } ] }
  ],
  "tests": [
    { "symbol": "ImpactTests.PipelineTests.ExecuteViaHelper", "project": "ImpactTests", "file": ".../PipelineTests.cs", "line": 15, "column": 17, "distance": 2,
      "path": ["ImpactTests.PipelineTests.ExecuteViaHelper", "ImpactTests.PipelineTests.RunTrim", "ImpactLib.Pipeline.Execute"] }
  ],
  "note": "At least one target is publicly visible: dependents were found only inside the loaded solution. External consumers (...) can't be seen."
}
```

Limits:
- Only the loaded solution is visible. Consumers in other repositories or packages are not, and
  a `note` says so when a target is publicly visible.
- Reflection, DI registration, serialization and source-generated wiring are invisible. A
  dependency that exists only at runtime won't show up.
- Test detection is by attribute name (`Fact`, `Theory`, `Test`, `TestCase`, `TestMethod`,
  `SkippableFact`, `SkippableTheory`). Tests discovered by convention or by custom attributes with
  other names are missed, and tests that reach the target only through reflection or DI are missed too.
- Interface and virtual dispatch over-approximates. A test that calls `Pipeline.Execute`
  is reported for every `IStage.Run` implementation, not only the one it passes at runtime.

### `apply_code_fix`

Applies one of Roslyn's own C# code fixes to every occurrence of a diagnostic id in a file,
project or the whole solution, then writes it through the same compile gate as every other
write tool (refused if it would introduce a new compile error, or if a touched file changed
on disk since load). Proven fixes: **CS8019** (remove unnecessary usings; `IDE0005` is accepted
as an alias) and **CS0246** (add the missing `using` when the type exists in exactly one
namespace in the solution). Uses the provider's FixAll where it supports the scope, otherwise
fixes one diagnostic at a time and composes the edits.

| Parameter | Default | Meaning |
|---|---|---|
| `diagnosticId` | (required) | e.g. `CS8019`, `CS0246` |
| `scope` | `solution` | `file`, `project` or `solution` |
| `filePath` | | required for `scope: file`; for `scope: project` selects the file's project |
| `projectName` | | for `scope: project` |
| `fixTitle` | first action | pick among several actions by title substring, e.g. `using FixtureLib.Imports` |
| `maxDiagnostics` | 500 | fix at most this many occurrences |
| `dryRun` | false | compute + gate, report the exact changes, write nothing |

Returns `diagnosticId, scope, diagnosticsFound, diagnosticsFixed, fixTitle, provider, applied,
dryRun, wouldApply, changedFileCount, changes, conflicts, staleFile, message`. `changes` lists
whole-line hunks per file (`{line, oldText, newText}`, texts include their line breaks). An id
with no Roslyn fixer returns `applied: false` with a "Not fixable" message; an id with no
occurrences in scope returns `diagnosticsFound: 0`.

```jsonc
// apply_code_fix { "diagnosticId": "CS8019", "scope": "file", "filePath": ".../Fixable.cs", "dryRun": true }
{ "diagnosticId": "CS8019", "scope": "file", "diagnosticsFound": 3, "diagnosticsFixed": 3,
  "fixTitle": "Remove unnecessary usings", "provider": "RemoveUnnecessaryImports", "fixAll": true,
  "applied": false, "dryRun": true, "wouldApply": true, "changedFileCount": 1,
  "changes": [{ "file": ".../Fixable.cs", "edits": [
    { "line": 1, "oldText": "using System.Text;\nusing System.Collections.Concurrent;\n", "newText": "" },
    { "line": 4, "oldText": "using System.Net;\n", "newText": "" } ] }],
  "message": "Dry run: the edit would be applied; 1 file(s) would change. Nothing was written." }
```

### `list_code_fixes` (read-only)

Lists the compiler diagnostics in a file, project or the solution, Hidden severity included
by default (so CS8019 unnecessary usings show up), grouped by id with a count and up to 20
locations each, and for each id whether a Roslyn code fix exists: `fixable`, `providers`,
`fixAllSupported` (and `fixedVia` when the fixer is registered under another id, as with
CS8019). Parameters: `diagnosticId?`, `scope` (`file` | `project` | `solution`, default
`solution`), `filePath?`, `projectName?`, `includeHidden` (default true). Ordering is
deterministic (ids ordinal; locations by file, then position). Analyzer-only IDE diagnostics
are not computed, and files under `bin/` and `obj/` are excluded.

### `move_type_to_namespace`

Moves one top-level type to another namespace across the whole solution. Every
declaration moves, including all partial parts across files, and every reference keeps
resolving:
- Fully-qualified references, qualified crefs, `using static` and alias targets are
  re-qualified from `Old.Ns.Type` to `New.Ns.Type`.
- Files that use the type by simple name get `using New.Ns;` if the namespace is not
  already in scope. This includes code that stays behind in the old namespace.

How each declaration moves:
- **The namespace declaration holds only this type:** the declaration is renamed. This
  works for file-scoped and block namespaces.
- **Other types share a block namespace:** the type, with its doc comments and
  attributes, moves into a new `namespace New.Ns { }` block at the end of the same file.
- **Other types share a file-scoped namespace:** C# allows only one namespace in that
  file, so the type moves to a new `<TypeName>.cs` next to it.

Names the moved type relied on from its old namespace get a `using`. If a type with the
same name in the new namespace would capture the simple name, the name is fully
qualified instead.

The tool refuses nested types, metadata types, a move to the namespace the type is
already in, and a target namespace that already holds a type of the same name. Comments
and string literals are never touched. `declarations` reports the mechanics used for
each file. As with every write tool, the result is compiled first and nothing is written
if it wouldn't build.

### `rename_namespace`

Renames a namespace across the whole solution. This covers every declaration (block and
file-scoped, in every project) and every `using`, `using static`, alias target,
qualified name and cref. Child namespaces move with it, so `A.B` → `A.C` also renames
`A.B.D` → `A.C.D`.

Renames that change only the last segment under the same parent use Roslyn's
`Renamer`. Anything else, such as `A.B` → `A.X.Y` or a merge into a parent, uses a
symbol-driven syntax pass. The result reports which path was used as `mechanics`.

The tool refuses in these cases:
- The new namespace already exists, in source or in a referenced assembly. The message
  names where. Pass `merge: true` to combine the two namespaces deliberately.
- A merge would put two types with the same name into one namespace.
- The old namespace has no declarations in source (for example `System.Text`).

Comments and string literals are never touched, and usings that a merge makes identical
are collapsed. Files and folders are not renamed. The result is compiled before
anything is written.

### `implement_interface`

Generates stubs for every interface member a class or struct does not implement yet, for the
interfaces in its base list (or only `interfaceName`), including members inherited from base
interfaces. Members already satisfied, including by a base class, are left alone and listed.
Stubs throw `NotImplementedException`, carry the interface member's XML doc comment (or
`/// <inheritdoc />`), and are appended to the type's first declaration (for a partial type the
result names the file). Only the inserted members are simplified and formatted; the rest of
the file is untouched. `using System;` is added only if `NotImplementedException` does not
already resolve (implicit usings count). Compile-gated like every write tool.

| Parameter | Default | Meaning |
|---|---|---|
| `filePath` + `line` + `column` | | the type, or any member of it |
| `fullyQualifiedName` | | instead of a position, e.g. `MyLib.MemoryRepository` |
| `interfaceName` | all | `IRepository`, `IRepository<string>` or fully-qualified |
| `explicitly` | false | emit explicit interface implementations |
| `dryRun` | false | compute + gate, report the exact changes, write nothing |

Returns `type, interfaces, alreadyImplemented, generated, insertedInto, partialDeclarationFiles,
addedUsingSystem, applied, dryRun, wouldApply, changedFileCount, changes, conflicts, staleFile,
message`. If nothing is missing it returns `applied: false` with "Nothing to generate".

```jsonc
// implement_interface { "fullyQualifiedName": "GenerationLib.MemoryRepository", "dryRun": true }
{ "type": { "name": "MemoryRepository", "kind": "NamedType", "display": "GenerationLib.MemoryRepository" },
  "interfaces": ["GenerationLib.IRepository<string>", "GenerationLib.IReadOnlyRepo<string>"],
  "alreadyImplemented": ["GenerationLib.IRepository<string>.Name", "GenerationLib.IRepository<string>.Add(string)"],
  "generated": ["GenerationLib.IRepository<string>.Find(string)", "GenerationLib.IRepository<string>.Current",
                "GenerationLib.IRepository<string>.Changed", "GenerationLib.IRepository<string>.this[int]",
                "GenerationLib.IReadOnlyRepo<string>.Count"],
  "insertedInto": ".../GenerationLib/Contracts.cs",
  "applied": false, "dryRun": true, "wouldApply": true, "changedFileCount": 1,
  "changes": [{ "file": ".../Contracts.cs", "edits": [{ "line": 37, "oldText": "",
    "newText": "\n    /// <summary>Finds the item stored under <paramref name=\"key\"/>.</summary>\n    /// <param name=\"key\">The lookup key.</param>\n    public string? Find(string key)\n    {\n        throw new NotImplementedException();\n    }\n ..." }] }],
  "message": "Dry run: the edit would be applied; 1 file(s) would change. Nothing was written." }
```

### `implement_abstract`

Generates `override` stubs for every abstract member inherited from the base-class chain that
the class does not override yet. Stubs keep the base member's accessibility, throw
`NotImplementedException`, and carry the abstract member's XML doc comment (or
`/// <inheritdoc />`). Works on an abstract class too. Refuses, writing nothing, if the class
has no abstract base; returns "Nothing to generate" if everything is already overridden.
Parameters: the type (`filePath` + `line` + `column`, or `fullyQualifiedName`) and `dryRun`.
Returns `type, abstractBases, generated, insertedInto, applied, dryRun, wouldApply,
changedFileCount, changes, conflicts, staleFile, message`.

```csharp
// implement_abstract { "fullyQualifiedName": "GenerationLib.Circle2" } appends to Circle2:
    /// <summary>Length of the outline.</summary>
    public override double Perimeter
    {
        get
        {
            throw new NotImplementedException();
        }
    }
```

### `generate_overrides`

Generates `override` members for virtual (non-abstract, non-sealed, not yet overridden)
members of the base chain, including `object`'s `Equals`, `GetHashCode` and `ToString`.
Bodies call the base member, so behaviour is unchanged until you edit them. Pass `members`
(names; every overload of a name is generated). Without `members` it **refuses** and lists the
`candidates`, unless `all: true`, so an agent never blanket-overrides `object` by accident.
Doc comments are copied as above.

| Parameter | Default | Meaning |
|---|---|---|
| `filePath` + `line` + `column` / `fullyQualifiedName` | | the type |
| `members` | | names to override, e.g. `["ToString"]` |
| `all` | false | override every candidate when `members` is not given |
| `dryRun` | false | compute + gate, report the exact changes, write nothing |

Returns `candidates` on refusal; `generated, insertedInto, applied, dryRun, wouldApply,
changedFileCount, changes, conflicts, staleFile, message` on success.

```jsonc
// generate_overrides { "fullyQualifiedName": "GenerationLib.Circle2" }
{ "type": { "name": "Circle2", ... },
  "candidates": ["GenerationLib.Shape2.Describe()", "object.Equals(object?)", "object.GetHashCode()", "object.ToString()"],
  "applied": false, "changedFileCount": 0, "changes": [],
  "message": "Not applied: pass members (names from candidates) or all: true. Refusing to override all 4 virtual member(s) by default. Nothing was written." }
```

```csharp
// generate_overrides { "fullyQualifiedName": "GenerationLib.Circle2", "members": ["ToString"] }
    /// <inheritdoc />
    public override string? ToString()
    {
        return base.ToString();
    }
```

### `get_symbol_info` (read-only)

Everything the compiler knows about one symbol, by position or fully-qualified name
(metadata symbols such as `System.String` work too): `kind` (class, record, struct,
interface, enum, delegate, method, constructor, local function, property, indexer,
field, const, event, parameter, local, namespace, ...), `accessibility`, `modifiers`
(only the ones that apply, in C# declaration order), `containingType`,
`containingNamespace`, `returnType` or `type`, `parameters` (name, type, default,
modifier), `typeParameters` with constraints, `baseType` and every interface (types),
`overrides` and `implements` (members), `declarations` (one per declaring location, so
partials give several; empty for metadata), `isFromMetadata`, the raw XML `docComment`,
and `attributes`. Everything is sorted, so the output is stable from run to run.

### `get_type_hierarchy` (read-only)

For a named type: `bases` walking up (a class ends with `System.Object`; an interface
lists its base interfaces), `interfaces` (all of them, flattened and sorted), and
`derived` walking down through the loaded solution: derived classes, derived interfaces
and implementations, transitive, breadth-first, each with `depth`, `relation` and the
parent it came through (`via`). Every entry carries `file` when it is declared in
source, or `isFromMetadata: true`. Capped by `maxNodes` (default 200, `truncated: true`
when hit). Pointing it at a member answers with the containing type's hierarchy and says
so in `note`.

### `find_unused_parameters` (read-only)

Parameters of source-declared methods, constructors and local functions that the body
never reads or writes, in the same two honesty tiers as `find_unused_members`:
**`highConfidence`** (private/internal and not virtual, or a local function, so every
caller is in the solution) and **`lowConfidence`** (externally visible or virtual, so
external callers or overriders may depend on the signature). `nameof(p)` and passing `p`
along both count as uses. Event-handler-shaped methods (`object sender, EventArgs e`)
are reported with `conventional: true`. Signatures that aren't the method's to change
are not listed, but `excludedCounts` says how many parameters were skipped and why:
`override`, `interfaceImplementation`, `noBody` (abstract, extern, partial definition,
interface member), `usedAsDelegate` (the method is referenced as a method group),
`entryPoint` (`Main`), `discard` (`_`, `_1`), `extensionThis`, `generatedCode`. Scope
with `projectName` or `filePath`. Capped by `maxResults` (default 200,
`truncated: true`). To act on a hit, use `change_signature`, which rewrites every call
site through the compile gate.

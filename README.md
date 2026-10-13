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

Positions are 1-based line/column. Out-of-range columns clamp to the line's end, because
agents give sloppy coordinates. All errors return as structured JSON (`{"error": ...}`),
never protocol faults.

The original nine tools are read-only: nothing they do modifies your code.
`apply_rename` and `extract_interface` write to disk; `find_unused_members` is
read-only. All three are covered in the "v2: tools that edit" section below.
v2.1 adds one more read-only tool (`find_callers`) and three more that write
(`delete_member`, `move_type_to_file`, `change_signature`), all under the same
compile-before-write contract; see "v2.1" at the end.

## Usage with Claude Code

Add to your project's `.mcp.json`:

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


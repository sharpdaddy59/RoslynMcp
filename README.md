# RoslynMcp

**Semantic C# tools for Claude, via MCP.**

Claude Code navigates C# codebases with text search — grep finds strings, not symbols.
RoslynMcp is an MCP server that gives Claude the compiler's understanding of your code:
real find-references, go-to-definition, implementations, diagnostics, and rename preview,
powered by Roslyn.

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

Positions are 1-based line/column. Out-of-range columns clamp to the line's end, because
agents give sloppy coordinates. All errors return as structured JSON (`{"error": ...}`),
never protocol faults. Read-only by design: nothing in this server modifies your code.

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

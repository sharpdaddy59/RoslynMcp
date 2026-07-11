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

Then in a session: ask Claude to `load_solution` your .sln and navigate semantically.

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

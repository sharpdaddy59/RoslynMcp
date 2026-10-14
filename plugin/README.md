# RoslynMcp Claude Code plugin

This plugin registers the `roslyn` MCP server, which gives Claude Roslyn-backed C# tools: find-references, go-to-definition, diagnostics, rename, and the rest listed in the [main README](../README.md).

```
/plugin marketplace add sharpdaddy59/RoslynMcp
/plugin install roslyn@roslynmcp
```

## How it bootstraps

The server command is `bash ${CLAUDE_PLUGIN_ROOT}/scripts/roslyn-mcp.sh`. Each time Claude Code starts the server, the script does this:

1. It looks for `roslyn-mcp` on `PATH`, then in `~/.dotnet/tools`. That folder is where `dotnet tool install -g` puts the tool, and non-login shells often leave it off `PATH`. If it finds the tool, it runs it.
2. If the tool is missing, it checks that `dotnet` is available. The .NET 10 SDK is needed in any case, because the server loads your solution through MSBuild.
3. It downloads `RoslynMcp.<version>.nupkg` from the latest [GitHub Release](https://github.com/sharpdaddy59/RoslynMcp/releases). It uses curl, or `gh` if curl is missing.
4. It runs `dotnet tool install -g RoslynMcp --version <version>`, using a private nuget.config whose only feed is the downloaded folder. On nuget.org the id `RoslynMcp` belongs to an unrelated project, so the script pins the version and feed.
5. It starts the newly installed tool.

All bootstrap output goes to stderr, because stdout carries the MCP JSON-RPC traffic.

## Environment override

To install from a local folder that holds `RoslynMcp.*.nupkg` instead of from GitHub, set `ROSLYN_MCP_NUPKG_DIR`. This is useful offline, for testing a locally packed build, or behind a proxy that blocks api.github.com.

## Updating the tool

The plugin installs the tool only when it is missing. It never upgrades an existing install. To move to a newer release:

```bash
gh release download -R sharpdaddy59/RoslynMcp -p '*.nupkg' -D /tmp/roslynmcp
dotnet tool update -g RoslynMcp --add-source /tmp/roslynmcp
```

## Windows

Native Claude Code on Windows requires Git for Windows. If Git's `bash.exe` is the `bash` found on `PATH`, the launcher should work as it does on Linux, though this is untested. It fails if `bash` is missing, or if `bash` resolves to WSL's `bash.exe` in System32, which runs inside the Linux distro and uses that distro's dotnet. In either case, `scripts/roslyn-mcp.ps1` does the same job in PowerShell. Plugin MCP declarations have no per-platform command, so the plugin cannot select the PowerShell script by itself. You have two options:

- Run the script once by hand to install the tool: `pwsh -File <plugin dir>\scripts\roslyn-mcp.ps1`, then press Ctrl+C once the server starts. After that, the bash launcher finds `roslyn-mcp.exe` in `%USERPROFILE%\.dotnet\tools`.
- Skip the plugin. Add a server with command `roslyn-mcp` to your project's `.mcp.json`, as the main README describes.

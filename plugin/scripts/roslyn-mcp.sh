#!/usr/bin/env bash
# Launches the roslyn-mcp stdio MCP server, installing the .NET global tool from the
# latest GitHub Release first if it is missing.
#
# stdout is the MCP JSON-RPC channel: everything this script says goes to stderr,
# and the only thing that ever writes to stdout is the server we exec at the end.
set -u

REPO="sharpdaddy59/RoslynMcp"
log() { printf 'roslyn-mcp bootstrap: %s\n' "$*" >&2; }

# `dotnet tool install -g` puts the shim in ~/.dotnet/tools, which is often not on
# PATH for the non-login shell Claude Code spawns, so look there too.
find_tool() {
  if command -v roslyn-mcp >/dev/null 2>&1; then command -v roslyn-mcp; return 0; fi
  local shim
  # The .exe form covers Git Bash on Windows, which Claude Code uses as its shell there.
  for shim in "${HOME}/.dotnet/tools/roslyn-mcp" "${HOME}/.dotnet/tools/roslyn-mcp.exe"; do
    if [ -x "$shim" ]; then printf '%s\n' "$shim"; return 0; fi
  done
  return 1
}

if tool="$(find_tool)"; then exec "$tool" "$@"; fi

if ! command -v dotnet >/dev/null 2>&1; then
  log "roslyn-mcp is not installed and the 'dotnet' CLI was not found."
  log "Install the .NET 10 SDK (https://dotnet.microsoft.com/download), then restart Claude Code."
  exit 1
fi

# Where the .nupkg comes from: a local folder (ROSLYN_MCP_NUPKG_DIR, for offline
# installs and testing) or the latest GitHub Release.
if [ -n "${ROSLYN_MCP_NUPKG_DIR:-}" ]; then
  src="$ROSLYN_MCP_NUPKG_DIR"
  log "using local package folder $src"
else
  src="$(mktemp -d "${TMPDIR:-/tmp}/roslyn-mcp.XXXXXX")" || exit 1
  log "roslyn-mcp not found; downloading the latest release of $REPO"
  if command -v curl >/dev/null 2>&1; then
    url="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
      | grep -o '"browser_download_url": *"[^"]*/RoslynMcp\.[^"/]*\.nupkg"' \
      | head -n1 | sed 's/.*"\(https[^"]*\)"$/\1/')"
    if [ -z "$url" ]; then log "no RoslynMcp .nupkg found on the latest release of $REPO"; exit 1; fi
    curl -fsSL -o "$src/${url##*/}" "$url" >&2 || { log "download failed: $url"; exit 1; }
  elif command -v gh >/dev/null 2>&1; then
    gh release download -R "$REPO" -p 'RoslynMcp.*.nupkg' -D "$src" >&2 \
      || { log "gh release download failed"; exit 1; }
  else
    log "need curl or gh to download the release, or set ROSLYN_MCP_NUPKG_DIR to a folder holding the .nupkg"
    exit 1
  fi
fi

# Pick the newest RoslynMcp.<version>.nupkg in the folder and pin that version.
# Pinning matters: the id "RoslynMcp" on nuget.org belongs to an unrelated project,
# so an unpinned install could resolve to it. The private nuget.config below also
# clears every other feed for the same reason.
pkg="$(ls -1 "$src"/RoslynMcp.*.nupkg 2>/dev/null | sort -V | tail -n1)"
if [ -z "$pkg" ]; then log "no RoslynMcp.*.nupkg in $src"; exit 1; fi
ver="$(basename "$pkg" .nupkg)"
ver="${ver#RoslynMcp.}"

cfgdir="$(mktemp -d "${TMPDIR:-/tmp}/roslyn-mcp-cfg.XXXXXX")" || exit 1
cat > "$cfgdir/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="roslynmcp-local" value="$(cd "$src" && pwd)" />
  </packageSources>
</configuration>
XML

log "installing RoslynMcp $ver as a .NET global tool"
# "install" fails if a copy is already registered (e.g. shadowed or broken); then update.
dotnet tool install -g RoslynMcp --version "$ver" --configfile "$cfgdir/nuget.config" >&2 \
  || dotnet tool update -g RoslynMcp --version "$ver" --configfile "$cfgdir/nuget.config" >&2 \
  || { log "dotnet tool install failed"; exit 1; }

if tool="$(find_tool)"; then exec "$tool" "$@"; fi
log "installed, but roslyn-mcp is still not on PATH or in ~/.dotnet/tools"
exit 1

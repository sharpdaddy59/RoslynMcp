# Launches the roslyn-mcp stdio MCP server on Windows, installing the .NET global tool
# from the latest GitHub Release first if it is missing. Same logic as roslyn-mcp.sh.
#
# stdout is the MCP JSON-RPC channel: everything this script says goes to stderr
# ([Console]::Error), and only the server itself writes to stdout.
$ErrorActionPreference = 'Stop'
$Repo = 'sharpdaddy59/RoslynMcp'
function Log($msg) { [Console]::Error.WriteLine("roslyn-mcp bootstrap: $msg") }

# `dotnet tool install -g` puts the shim in %USERPROFILE%\.dotnet\tools, which may not
# be on PATH for the process Claude Code spawns, so look there too.
function Find-Tool {
    $cmd = Get-Command roslyn-mcp -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $shim = Join-Path $HOME '.dotnet\tools\roslyn-mcp.exe'
    if (Test-Path $shim) { return $shim }
    return $null
}

function Start-Server($path) {
    # Run the server with this script's stdin/stdout; propagate its exit code.
    & $path @args
    exit $LASTEXITCODE
}

$tool = Find-Tool
if ($tool) { Start-Server $tool @args }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Log "roslyn-mcp is not installed and the 'dotnet' CLI was not found."
    Log "Install the .NET 10 SDK (https://dotnet.microsoft.com/download), then restart Claude Code."
    exit 1
}

# Where the .nupkg comes from: a local folder (ROSLYN_MCP_NUPKG_DIR) or the latest release.
if ($env:ROSLYN_MCP_NUPKG_DIR) {
    $src = $env:ROSLYN_MCP_NUPKG_DIR
    Log "using local package folder $src"
} else {
    $src = Join-Path ([IO.Path]::GetTempPath()) ("roslyn-mcp." + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $src | Out-Null
    Log "roslyn-mcp not found; downloading the latest release of $Repo"
    $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest"
    $asset = $release.assets | Where-Object { $_.name -like 'RoslynMcp.*.nupkg' } | Select-Object -First 1
    if (-not $asset) { Log "no RoslynMcp .nupkg found on the latest release of $Repo"; exit 1 }
    Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $src $asset.name) -UseBasicParsing
}

# Pin the version from the file name: the id "RoslynMcp" on nuget.org is an unrelated
# project, so we also install from a private nuget.config that clears every other feed.
$pkg = Get-ChildItem $src -Filter 'RoslynMcp.*.nupkg' |
    Sort-Object { [version](($_.BaseName -replace '^RoslynMcp\.', '') -replace '-.*$', '') } |
    Select-Object -Last 1
if (-not $pkg) { Log "no RoslynMcp.*.nupkg in $src"; exit 1 }
$ver = $pkg.BaseName -replace '^RoslynMcp\.', ''

$cfgDir = Join-Path ([IO.Path]::GetTempPath()) ("roslyn-mcp-cfg." + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $cfgDir | Out-Null
$cfg = Join-Path $cfgDir 'nuget.config'
$full = (Resolve-Path $src).Path
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="roslynmcp-local" value="$full" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $cfg -Encoding UTF8

Log "installing RoslynMcp $ver as a .NET global tool"
# "install" fails if a copy is already registered; then try "update". Tool output -> stderr.
dotnet tool install -g RoslynMcp --version $ver --configfile $cfg 2>&1 | ForEach-Object { [Console]::Error.WriteLine($_) }
if ($LASTEXITCODE -ne 0) {
    dotnet tool update -g RoslynMcp --version $ver --configfile $cfg 2>&1 | ForEach-Object { [Console]::Error.WriteLine($_) }
    if ($LASTEXITCODE -ne 0) { Log "dotnet tool install failed"; exit 1 }
}

$tool = Find-Tool
if ($tool) { Start-Server $tool @args }
Log "installed, but roslyn-mcp is still not on PATH or in ~/.dotnet/tools"
exit 1

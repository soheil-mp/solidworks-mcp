<#
.SYNOPSIS
  Builds solidworks-mcp, publishes it into a versioned folder and registers it with Claude Code (user scope).

.DESCRIPTION
  Publishing into %LOCALAPPDATA%\solidworks-mcp\bin\<version>-<timestamp> means a running server (locked exe) never
  blocks an update, and rolling back is just re-registering an older folder.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1
  powershell -ExecutionPolicy Bypass -File tools\install.ps1 -NoRegister
#>
param(
    [switch]$NoRegister,
    [string]$Name = 'solidworks',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download'
}
$sdks = (& dotnet --list-sdks) -join "`n"
if ($sdks -notmatch '^10\.') { Write-Warning "No .NET 10 SDK found in:`n$sdks" }

$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $env:LOCALAPPDATA "solidworks-mcp\bin\$version-$stamp"

Write-Host "Publishing solidworks-mcp $version to $out"
& dotnet publish (Join-Path $repo 'src\SolidWorksMcp.Server\SolidWorksMcp.Server.csproj') -c $Configuration -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$exe = Join-Path $out 'solidworks-mcp.exe'
& $exe --check-catalog
if ($LASTEXITCODE -ne 0) { throw 'The tool catalog check failed.' }

if (-not $NoRegister) {
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        & claude mcp remove $Name -s user 2>$null | Out-Null
        & claude mcp add --scope user $Name -- $exe
        Write-Host "Registered '$Name' with Claude Code (user scope)."
    }
    else {
        Write-Warning "Claude Code CLI not found; register manually: claude mcp add --scope user $Name -- `"$exe`""
    }
}

$desktop = @{ mcpServers = @{ $Name = @{ command = $exe } } } | ConvertTo-Json -Depth 4
Write-Host "`nClaude Desktop snippet (%APPDATA%\Claude\claude_desktop_config.json):`n$desktop"
Write-Host "`nIf Smart App Control blocks the exe, use: claude mcp add --scope user $Name -- dotnet `"$(Join-Path $out 'solidworks-mcp.dll')`""

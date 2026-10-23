<#
.SYNOPSIS
  Builds solidworks-mcp, publishes it into a versioned folder and registers it with Claude Code (user scope).

.DESCRIPTION
  Publishing into %LOCALAPPDATA%\solidworks-mcp\bin\<version>-<timestamp> means a running server (locked exe) never
  blocks an update, and rolling back is just re-registering an older folder.

  Smart App Control (or another application-control policy) can refuse to start the freshly built, unsigned exe. The
  script then checks and registers `dotnet solidworks-mcp.dll` instead, which runs through the Microsoft-signed host;
  -Dotnet goes that way from the start.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1
  powershell -ExecutionPolicy Bypass -File tools\install.ps1 -NoRegister
  powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Dotnet
#>
param(
    [switch]$NoRegister,
    [switch]$Dotnet,
    [string]$Name = 'solidworks',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download'
}
$sdks = (& dotnet --list-sdks) -join "`n"
if ($sdks -notmatch '(?m)^10\.') { Write-Warning "No .NET 10 SDK found in:`n$sdks" }

$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $env:LOCALAPPDATA "solidworks-mcp\bin\$version-$stamp"

Write-Host "Publishing solidworks-mcp $version to $out"
& dotnet publish (Join-Path $repo 'src\SolidWorksMcp.Server\SolidWorksMcp.Server.csproj') -c $Configuration -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$exe = Join-Path $out 'solidworks-mcp.exe'
$dll = Join-Path $out 'solidworks-mcp.dll'
$command = @($exe)
if (-not $Dotnet) {
    try { & $exe --check-catalog }
    catch {
        Write-Warning "Windows would not start $exe ($($_.Exception.Message)); using the dotnet host instead."
        $Dotnet = $true
    }
}
if ($Dotnet) {
    $command = @('dotnet', $dll)
    & dotnet $dll --check-catalog
}
if ($LASTEXITCODE -ne 0) {
    throw 'The tool catalog check failed. If a DLL was blocked ("An Application Control policy has blocked this file"), run the script again: a new build gets a new verdict.'
}

if (-not $NoRegister) {
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        # Windows PowerShell turns redirected stderr into errors; a missing registration is fine here.
        $ErrorActionPreference = 'Continue'
        & claude mcp remove $Name -s user *> $null
        $ErrorActionPreference = 'Stop'
        & claude mcp add --scope user $Name -- @command
        if ($LASTEXITCODE -ne 0) { throw "claude mcp add failed for '$Name'." }
        Write-Host "Registered '$Name' with Claude Code (user scope): $($command -join ' ')"
    }
    else {
        $quoted = ($command | ForEach-Object { '"' + $_ + '"' }) -join ' '
        Write-Warning "Claude Code CLI not found; register manually: claude mcp add --scope user $Name -- $quoted"
    }
}

$server = [ordered]@{ command = $command[0] }
if ($command.Count -gt 1) { $server.args = @($command | Select-Object -Skip 1) }
$desktop = @{ mcpServers = @{ $Name = $server } } | ConvertTo-Json -Depth 4
Write-Host "`nClaude Desktop snippet (%APPDATA%\Claude\claude_desktop_config.json):`n$desktop"

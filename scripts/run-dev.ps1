#Requires -Version 5.1
<#
.SYNOPSIS
    Shows the MCP registration status and starts the Kennect Windows host.

.DESCRIPTION
    Prints whether .mcp.json registers kennect-computer, whether the built MCP
    entry and host executable exist, and the M1 and M2 tool names. Then starts
    the host so the tray icon is visible. The host keeps running after this script
    returns; stop it from the tray Exit item.

.PARAMETER NoHost
    Only print the registration status; do not start the host. Useful for
    automated checks that must leave no process behind.
#>
[CmdletBinding()]
param(
    [switch]$NoHost
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$McpConfigPath = Join-Path $RepoRoot '.mcp.json'
$McpServerEntry = Join-Path $RepoRoot 'packages\mcp-server\dist\index.js'
$HostExe = Join-Path $RepoRoot 'packages\host-windows\KennectComputer.Host\bin\Debug\net10.0-windows\KennectComputer.Host.exe'
$HostConfig = Join-Path $RepoRoot 'config\default.json'
$ServerName = 'kennect-computer'
$ToolNames = @(
    'computer_status',
    'computer_windows',
    'computer_select',
    'computer_snapshot',
    'computer_focus',
    'computer_stop',
    'computer_click',
    'computer_type',
    'computer_key',
    'computer_scroll',
    'computer_find',
    'computer_wait',
    'computer_read_text',
    'computer_batch'
)

function Get-PropertyValue {
    param($Object, [string]$Name)

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Write-PathStatus {
    param([string]$Label, [string]$Path)

    $state = 'missing'
    if (Test-Path -LiteralPath $Path) {
        $state = 'found'
    }

    $displayLabel = $Label + ':'
    Write-Host ('  {0,-18} {1} [{2}]' -f $displayLabel, $Path, $state)
}

# The Windows host always publishes session.json/session.token under
# %LOCALAPPDATA%\KennectComputer; it does not read KENNECT_COMPUTER_SESSION_DIR.
$HostStateDir = $env:LOCALAPPDATA
if ([string]::IsNullOrWhiteSpace($HostStateDir)) {
    $HostStateDir = Join-Path $HOME 'AppData\Local'
}
$HostStateDir = Join-Path $HostStateDir 'KennectComputer'
$SessionFile = Join-Path $HostStateDir 'session.json'

# The MCP server does honour the override for discovery and audit. If it points
# elsewhere, the server cannot see the host's session files.
$DiscoveryDir = $HostStateDir
$DiscoveryOverride = $env:KENNECT_COMPUTER_SESSION_DIR
if (-not [string]::IsNullOrWhiteSpace($DiscoveryOverride)) {
    try {
        $DiscoveryDir = [System.IO.Path]::GetFullPath($DiscoveryOverride)
    }
    catch {
        $DiscoveryDir = $DiscoveryOverride
    }
}

Write-Host 'Kennect Computer - MCP registration status'
Write-PathStatus 'MCP config' $McpConfigPath
Write-PathStatus 'MCP server entry' $McpServerEntry
Write-PathStatus 'Host executable' $HostExe
Write-PathStatus 'Host config' $HostConfig
Write-PathStatus 'Host state dir' $HostStateDir

if (-not [string]::Equals($DiscoveryDir, $HostStateDir, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-PathStatus 'MCP discovery dir' $DiscoveryDir
    Write-Warning ("KENNECT_COMPUTER_SESSION_DIR is set, but the host always publishes its session files under {0}. The MCP server will look in {1}, so tool calls will fail with HOST_UNAVAILABLE. Unset the variable for a working connection." -f $HostStateDir, $DiscoveryDir)
}

$registered = $false
$registeredEntry = $null
if (Test-Path -LiteralPath $McpConfigPath) {
    try {
        $raw = [System.IO.File]::ReadAllText($McpConfigPath)
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $document = $raw | ConvertFrom-Json
            $servers = Get-PropertyValue $document 'mcpServers'
            $registeredEntry = Get-PropertyValue $servers $ServerName
            if ($null -ne $registeredEntry) {
                $registered = $true
            }
        }
    }
    catch {
        Write-Warning "Could not parse $McpConfigPath. Run scripts/install-dev.ps1 to repair it."
    }
}

if ($registered) {
    Write-Host ("  {0,-18} configured" -f 'Registration:')
}
else {
    Write-Host ("  {0,-18} NOT configured" -f 'Registration:')
    Write-Host '  Run scripts/install-dev.ps1 to add kennect-computer to .mcp.json.'
}

Write-Host ''
Write-Host 'Tools:'
foreach ($toolName in $ToolNames) {
    Write-Host "  - $toolName"
}

if ($NoHost) {
    Write-Host ''
    Write-Host 'Skipping host launch (-NoHost).'
    exit 0
}

if (-not (Test-Path -LiteralPath $HostExe)) {
    throw "Host executable not found: $HostExe. Run scripts/install-dev.ps1 without -SkipBuild first."
}

Write-Host ''
Write-Host 'Starting the Kennect host (tray visible)...'
$hostProcess = Start-Process -FilePath $HostExe -WorkingDirectory $RepoRoot -PassThru
Write-Host ('Host started with PID {0}.' -f $hostProcess.Id)

$deadline = (Get-Date).AddSeconds(5)
while (-not (Test-Path -LiteralPath $SessionFile) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 100
}

if (Test-Path -LiteralPath $SessionFile) {
    Write-Host "Host session published: $SessionFile"
}
else {
    Write-Warning "The host did not publish $SessionFile within 5 seconds. Check the tray and the host's diagnostics."
}

Write-Host ''
Write-Host 'The host keeps running with a tray icon. Stop it from the tray Exit item.'
Write-Host 'Press Ctrl+Alt+Shift+Esc to stop an in-flight desktop operation.'
Write-Host 'Restart Claude Code in this repository if .mcp.json changed, then ask it to call computer_status.'

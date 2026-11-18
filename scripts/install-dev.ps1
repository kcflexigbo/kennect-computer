#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Kennect Computer and registers the MCP server with Claude Code.

.DESCRIPTION
    Installs npm workspace dependencies, builds the shared protocol, MCP server,
    and Windows host, then writes the repository-root .mcp.json.

    The install is idempotent (R23): an existing kennect-computer entry is
    replaced in place, other MCP servers are preserved, and the previous file is
    backed up to .mcp.json.bak (or a timestamped .mcp.json.bak-*).

.PARAMETER SkipInstall
    Skip `npm install` (dependencies are already installed).

.PARAMETER SkipBuild
    Skip `npm run build` and `dotnet build` (build outputs already exist).
#>
[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$McpConfigPath = Join-Path $RepoRoot '.mcp.json'
$PackageJsonPath = Join-Path $RepoRoot 'package.json'
$McpServerEntry = Join-Path $RepoRoot 'packages\mcp-server\dist\index.js'
$HostProject = Join-Path $RepoRoot 'packages\host-windows\KennectComputer.Host\KennectComputer.Host.csproj'
$HostExe = Join-Path $RepoRoot 'packages\host-windows\KennectComputer.Host\bin\Debug\net10.0-windows\KennectComputer.Host.exe'
$HostConfig = Join-Path $RepoRoot 'config\default.json'
$ServerName = 'kennect-computer'
$ToolNames = @(
    'computer_status',
    'computer_windows',
    'computer_select',
    'computer_snapshot',
    'computer_focus',
    'computer_stop'
)

if (-not (Test-Path -LiteralPath $PackageJsonPath)) {
    throw "Run this script from a Kennect Computer checkout; package.json was not found at $RepoRoot."
}

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

function Read-McpDocument {
    param([string]$Path)

    $result = [pscustomobject]@{
        Document  = $null
        Malformed = $false
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return $result
    }

    $raw = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $result
    }

    try {
        $parsed = $raw | ConvertFrom-Json
        if ($parsed -is [System.Management.Automation.PSCustomObject]) {
            $result.Document = $parsed
        }
        else {
            $result.Malformed = $true
        }
    }
    catch {
        $result.Malformed = $true
    }

    return $result
}

function Test-KennectEntry {
    param($Document, [string]$ServerEntry, [string]$HostPath, [string]$ConfigPath)

    $servers = Get-PropertyValue $Document 'mcpServers'
    if (-not ($servers -is [System.Management.Automation.PSCustomObject])) {
        return $false
    }

    $entry = Get-PropertyValue $servers $ServerName
    if (-not ($entry -is [System.Management.Automation.PSCustomObject])) {
        return $false
    }

    $command = [string](Get-PropertyValue $entry 'command')
    if (-not [string]::Equals($command, 'node', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $argumentsProperty = $entry.PSObject.Properties['args']
    if ($null -eq $argumentsProperty -or -not ($argumentsProperty.Value -is [System.Array])) {
        return $false
    }

    $arguments = @($argumentsProperty.Value)
    if ($arguments.Count -ne 1) {
        return $false
    }
    if (-not [string]::Equals([string]$arguments[0], $ServerEntry, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $environment = Get-PropertyValue $entry 'env'
    if (-not ($environment -is [System.Management.Automation.PSCustomObject])) {
        return $false
    }

    $hostPath = [string](Get-PropertyValue $environment 'KENNECT_HOST_PATH')
    if (-not [string]::Equals($hostPath, $HostPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $configPath = [string](Get-PropertyValue $environment 'KENNECT_COMPUTER_CONFIG')
    if (-not [string]::Equals($configPath, $ConfigPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    return $true
}

function Set-KennectEntry {
    param($Document, [string]$ServerEntry, [string]$HostPath, [string]$ConfigPath)

    if (-not ($Document -is [System.Management.Automation.PSCustomObject])) {
        $Document = [pscustomobject]@{}
    }

    $serversProperty = $Document.PSObject.Properties['mcpServers']
    if ($null -ne $serversProperty -and $serversProperty.Value -is [System.Management.Automation.PSCustomObject]) {
        $servers = $serversProperty.Value
    }
    else {
        $servers = [pscustomobject]@{}
        if ($null -eq $serversProperty) {
            $Document | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value $servers
        }
        else {
            $serversProperty.Value = $servers
        }
    }

    # Preserve any user-added environment variables on our entry; update the
    # two managed paths in place.
    $environment = [pscustomobject]@{}
    $existingEntry = Get-PropertyValue $servers $ServerName
    $existingEnvironment = Get-PropertyValue $existingEntry 'env'
    if ($existingEnvironment -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $existingEnvironment.PSObject.Properties) {
            $environment | Add-Member -MemberType NoteProperty -Name $property.Name -Value $property.Value
        }
    }

    $managedEnvironment = @(
        @{ Name = 'KENNECT_HOST_PATH'; Value = $HostPath },
        @{ Name = 'KENNECT_COMPUTER_CONFIG'; Value = $ConfigPath }
    )
    foreach ($item in $managedEnvironment) {
        $existingProperty = $environment.PSObject.Properties[$item.Name]
        if ($null -eq $existingProperty) {
            $environment | Add-Member -MemberType NoteProperty -Name $item.Name -Value $item.Value
        }
        else {
            $existingProperty.Value = $item.Value
        }
    }

    $entry = [pscustomobject]@{
        command = 'node'
        args    = @($ServerEntry)
        env     = $environment
    }

    $entryProperty = $servers.PSObject.Properties[$ServerName]
    if ($null -eq $entryProperty) {
        $servers | Add-Member -MemberType NoteProperty -Name $ServerName -Value $entry
    }
    else {
        $entryProperty.Value = $entry
    }

    return $Document
}

function Backup-McpConfig {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $backupPath = "$Path.bak"
    if (Test-Path -LiteralPath $backupPath) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
        $backupPath = "$Path.bak-$stamp"
    }

    Copy-Item -LiteralPath $Path -Destination $backupPath
    return $backupPath
}

if (-not $SkipInstall) {
    Write-Host '==> npm install'
    & npm install
    if ($LASTEXITCODE -ne 0) {
        throw "npm install failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipBuild) {
    Write-Host '==> npm run build'
    & npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build failed with exit code $LASTEXITCODE."
    }

    Write-Host '==> dotnet build packages/host-windows/KennectComputer.Host'
    & dotnet build $HostProject --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

foreach ($requiredPath in @($McpServerEntry, $HostExe, $HostConfig)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Expected build output was not found: $requiredPath. Run this script without -SkipBuild."
    }
}

$read = Read-McpDocument -Path $McpConfigPath
$alreadyRegistered = Test-KennectEntry -Document $read.Document -ServerEntry $McpServerEntry -HostPath $HostExe -ConfigPath $HostConfig

if ($alreadyRegistered) {
    Write-Host "MCP registration is already up to date: $McpConfigPath"
}
else {
    if ($read.Malformed) {
        Write-Warning "The existing .mcp.json is not a JSON object; it will be backed up before the new registration is written."
    }

    $document = Set-KennectEntry -Document $read.Document -ServerEntry $McpServerEntry -HostPath $HostExe -ConfigPath $HostConfig
    $json = ($document | ConvertTo-Json -Depth 20) + [Environment]::NewLine

    $backupPath = Backup-McpConfig -Path $McpConfigPath
    if ($null -ne $backupPath) {
        Write-Host "Backed up the previous .mcp.json to $backupPath"
    }

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($McpConfigPath, $json, $utf8)
    Write-Host "Wrote MCP registration: $McpConfigPath"
}

Write-Host ''
Write-Host 'MCP registration is ready. Restart Claude Code in this repository so it loads .mcp.json.'
Write-Host ''
Write-Host 'Recommended Claude Code allowlist (merge into .claude/settings.json):'
Write-Host '{'
Write-Host '  "permissions": {'
Write-Host '    "allow": ['
for ($index = 0; $index -lt $ToolNames.Count; $index++) {
    $comma = ','
    if ($index -eq $ToolNames.Count - 1) {
        $comma = ''
    }
    Write-Host ('      "mcp__kennect-computer__' + $ToolNames[$index] + '"' + $comma)
}
Write-Host '    ]'
Write-Host '  }'
Write-Host '}'
Write-Host ''
Write-Host 'Next: powershell -ExecutionPolicy Bypass -File scripts/run-dev.ps1'

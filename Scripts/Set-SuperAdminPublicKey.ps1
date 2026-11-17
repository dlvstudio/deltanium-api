<#
.SYNOPSIS
  Install / rotate SUPER_ADMIN_PUBLIC_KEY on IIS (API + blockers) via ProgramData DPAPI secrets.

.DESCRIPTION
  Run as Administrator on the Windows IIS server.
  Merges into existing %ProgramData%\Deltanium\<site>\env.dpapi.json (does not wipe other keys).
  Private key must NEVER be passed to this script or stored on the server.

.EXAMPLE
  .\Set-SuperAdminPublicKey.ps1 -PublicKey "02abc..."

.EXAMPLE
  .\Set-SuperAdminPublicKey.ps1 -KeysJson "C:\temp\super_admin_keys.json"
#>
#requires -RunAsAdministrator
param(
    [string] $PublicKey = "",

    [string] $KeysJson = "",

    [string[]] $Sites = @(
        "api.deltanium.com",
        "blocker-1.deltanium.com"
    ),

    # Also write App Pool environmentVariables (legacy path). Prefer ProgramData.
    [switch] $AlsoIisAppPoolEnv,

    [switch] $SkipRecycle
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

function Protect-Dpapi([string] $Plain) {
    $b = [Text.Encoding]::UTF8.GetBytes($Plain)
    $p = [Security.Cryptography.ProtectedData]::Protect(
        $b, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return "dpapi:" + [Convert]::ToBase64String($p)
}

function Resolve-PublicKey {
    if (-not [string]::IsNullOrWhiteSpace($PublicKey)) {
        return $PublicKey.Trim()
    }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($KeysJson)) {
        $candidates += $KeysJson
    }
    $candidates += @(
        (Join-Path $PSScriptRoot "super_admin_keys.json"),
        (Join-Path $env:USERPROFILE ".deltanium\super_admin_keys.json"),
        "C:\temp\super_admin_keys.json"
    )

    foreach ($path in $candidates) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path $path)) { continue }
        $info = Get-Content -Raw $path | ConvertFrom-Json
        if ($info.PublicKey) {
            Write-Host "Read PublicKey from $path"
            if ($info.PrivateKey) {
                Write-Host "WARNING: JSON contains PrivateKey. Do not leave this file on the server; delete after running." -ForegroundColor Yellow
            }
            return [string]$info.PublicKey.Trim()
        }
    }

    throw "Pass -PublicKey or -KeysJson (file with PublicKey). Private key must stay offline."
}

function Merge-ProgramDataSecret([string] $Site, [string] $Name, [string] $DpapiValue) {
    $dir = Join-Path $env:ProgramData "Deltanium\$Site"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir "env.dpapi.json"

    $map = [ordered]@{}
    if (Test-Path $path) {
        $existing = Get-Content -Raw $path | ConvertFrom-Json
        foreach ($p in $existing.PSObject.Properties) {
            $map[$p.Name] = $p.Value
        }
    }
    $map[$Name] = $DpapiValue

    $obj = [pscustomobject]@{}
    foreach ($k in $map.Keys) {
        $obj | Add-Member -NotePropertyName $k -NotePropertyValue $map[$k] -Force
    }
    ($obj | ConvertTo-Json -Depth 4) | Set-Content -Path $path -Encoding UTF8

    $pool = "IIS AppPool\$Site"
    icacls $dir /grant "${pool}:(OI)(CI)R" /T | Out-Null
    icacls $path /grant "${pool}:R" | Out-Null
    Write-Host "OK ProgramData $path <- $Name"
}

$pk = Resolve-PublicKey
if ($pk.Length -lt 66 -or ($pk.Substring(0, 2) -notin @("02", "03", "04"))) {
    Write-Host "WARNING: PublicKey length/prefix looks unusual ($($pk.Length) chars). Continuing anyway." -ForegroundColor Yellow
}

$protected = Protect-Dpapi $pk

foreach ($site in $Sites) {
    Merge-ProgramDataSecret -Site $site -Name "SUPER_ADMIN_PUBLIC_KEY" -DpapiValue $protected

    if ($AlsoIisAppPoolEnv) {
        $setSecret = Join-Path $PSScriptRoot "Set-DeltaniumIisSecret.ps1"
        if (-not (Test-Path $setSecret)) {
            throw "Missing $setSecret (needed for -AlsoIisAppPoolEnv)"
        }
        & $setSecret -AppPool $site -Name "SUPER_ADMIN_PUBLIC_KEY" -PlainValue $pk
    }

    if (-not $SkipRecycle) {
        Import-Module WebAdministration -ErrorAction Stop
        if (Get-WebAppPoolState -Name $site -ErrorAction SilentlyContinue) {
            Restart-WebAppPool -Name $site
            Write-Host "Recycled AppPool $site"
        }
        else {
            Write-Host "AppPool '$site' not found - skip recycle" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "Done. SUPER_ADMIN_PUBLIC_KEY updated on: $($Sites -join ', ')"
Write-Host "PublicKey (verify): $pk"
Write-Host "Private key was not written. Keep it only on the Super Admin laptop / PDF backup."

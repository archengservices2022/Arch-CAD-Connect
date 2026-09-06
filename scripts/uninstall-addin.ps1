<#
.SYNOPSIS
  Remove the Arch Engineering CAD Connect add-in installed by install-addin.ps1
  (per-user COM registration + .addin manifests). Does not require admin.

.EXAMPLE
  pwsh ./scripts/uninstall-addin.ps1
#>
[CmdletBinding()]
param(
  [string[]] $InventorVersions,
  [switch] $KeepBuildOutput
)

$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ClassId  = '93C45016-BDCA-4734-BB1A-4A06C36E900C'
$ProgId   = 'Arch.CadConnect.Inventor.ArchAddInServer'

if (-not $InventorVersions -or $InventorVersions.Count -eq 0) {
  $InventorVersions = Get-ChildItem (Join-Path $env:APPDATA 'Autodesk') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^Inventor (20\d\d)$' } | ForEach-Object { $Matches[1] }
}

foreach ($v in $InventorVersions) {
  $f = Join-Path $env:APPDATA "Autodesk/Inventor $v/Addins/Arch.CadConnect.addin"
  if (Test-Path $f) { Remove-Item $f -Force; Write-Host "removed $f" }
  # Inventor also remembers per-user load state here.
  $k = "HKCU:\Software\Autodesk\Inventor\Addins\{$ClassId}"
  if (Test-Path $k) { Remove-Item $k -Recurse -Force; Write-Host "removed $k" }
}

Remove-Item "HKCU:\Software\Classes\CLSID\{$ClassId}" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Classes\$ProgId" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "removed per-user COM registration for {$ClassId}"

if (-not $KeepBuildOutput) {
  $out = Join-Path $RepoRoot 'build/output'
  if (Test-Path $out) { Remove-Item $out -Recurse -Force; Write-Host "removed $out" }
}

Write-Host "`nDone. Restart Inventor to unload the add-in."
Write-Host "NOTE: the local session file (%LOCALAPPDATA%\ArchEngineering\CadConnect) is left in place;"
Write-Host "use the ribbon 'Sign Out' first if you want the server session revoked."

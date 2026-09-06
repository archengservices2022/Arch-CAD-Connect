<#
.SYNOPSIS
  Build, publish, COM-register (per-user, no admin) and manifest-install the
  Arch Engineering CAD Connect add-in for one or more Inventor versions.

.DESCRIPTION
  Steps:
    1. dotnet publish src/Arch.CadConnect.Inventor  (Release, framework-dependent)
    2. per-user COM registration of the generated comhost under
       HKCU\Software\Classes\CLSID\{ClassId}  (regsvr32 is NOT used, so no admin)
    3. render Manifest/Arch.CadConnect.addin.template with the absolute path of
       the published Arch.CadConnect.Inventor.dll and drop it into
       %APPDATA%\Autodesk\Inventor <version>\Addins\

  Re-run any time after a code change - it is idempotent.

.EXAMPLE
  pwsh ./scripts/install-addin.ps1
  pwsh ./scripts/install-addin.ps1 -InventorVersions 2025,2026 -Configuration Release
#>
[CmdletBinding()]
param(
  [string[]] $InventorVersions,
  [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Project    = Join-Path $RepoRoot 'src/Arch.CadConnect.Inventor/Arch.CadConnect.Inventor.csproj'
$Template   = Join-Path $RepoRoot 'src/Arch.CadConnect.Inventor/Manifest/Arch.CadConnect.addin.template'
$PublishDir = Join-Path $RepoRoot ("build/output/{0}/net8.0-windows" -f $Configuration)
$ClassId    = '93C45016-BDCA-4734-BB1A-4A06C36E900C'
$ProgId     = 'Arch.CadConnect.Inventor.ArchAddInServer'

function Get-InstalledInventorVersions {
  Get-ChildItem 'C:\Program Files\Autodesk' -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^Inventor (20\d\d)$' } |
    ForEach-Object { $Matches[1] }
}

if (-not $InventorVersions -or $InventorVersions.Count -eq 0) {
  $InventorVersions = @(Get-InstalledInventorVersions)
  if ($InventorVersions.Count -eq 0) {
    throw "No 'Inventor 20xx' install found under C:\Program Files\Autodesk. Pass -InventorVersions explicitly."
  }
}
Write-Host "Target Inventor version(s): $($InventorVersions -join ', ')"

# --- 1. publish ------------------------------------------------------------
Write-Host "`n[1/3] dotnet publish ($Configuration) ..."
& dotnet publish $Project -c $Configuration -o $PublishDir --nologo | Write-Host
$AddInDll  = Join-Path $PublishDir 'Arch.CadConnect.Inventor.dll'
$ComHost   = Join-Path $PublishDir 'Arch.CadConnect.Inventor.comhost.dll'
foreach ($p in @($AddInDll, $ComHost)) {
  if (-not (Test-Path $p)) { throw "Expected build output missing: $p" }
}

# --- 2. per-user COM registration ---------------------------------------
Write-Host "`n[2/3] per-user COM registration (HKCU, no admin) ..."
$clsidKey = "HKCU:\Software\Classes\CLSID\{$ClassId}"
New-Item -Path $clsidKey -Force | Out-Null
Set-ItemProperty -Path $clsidKey -Name '(default)' -Value $ProgId
New-Item -Path "$clsidKey\InprocServer32" -Force | Out-Null
Set-ItemProperty -Path "$clsidKey\InprocServer32" -Name '(default)' -Value $ComHost
Set-ItemProperty -Path "$clsidKey\InprocServer32" -Name 'ThreadingModel' -Value 'Both'
New-Item -Path "$clsidKey\ProgID" -Force | Out-Null
Set-ItemProperty -Path "$clsidKey\ProgID" -Name '(default)' -Value $ProgId

$progIdKey = "HKCU:\Software\Classes\$ProgId"
New-Item -Path "$progIdKey\CLSID" -Force | Out-Null
Set-ItemProperty -Path "$progIdKey\CLSID" -Name '(default)' -Value "{$ClassId}"
Write-Host "  registered {$ClassId} -> $ComHost"

# --- 3. .addin manifest per Inventor version -----------------------------
Write-Host "`n[3/3] installing .addin manifest(s) ..."
$manifestBody = (Get-Content -Raw $Template).Replace('%ASSEMBLY_PATH%', $AddInDll)
foreach ($v in $InventorVersions) {
  $dir = Join-Path $env:APPDATA "Autodesk/Inventor $v/Addins"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $dest = Join-Path $dir 'Arch.CadConnect.addin'
  Set-Content -Path $dest -Value $manifestBody -Encoding UTF8
  Write-Host "  $dest"
}

Write-Host "`nDone. Start Inventor $($InventorVersions[0]) - the 'ARCH ENGINEERING' ribbon tab should appear."
Write-Host "If it does not: Tools > Add-Ins, check 'Arch Engineering CAD Connect', set 'Loaded/Unloaded' + 'Load Automatically'."

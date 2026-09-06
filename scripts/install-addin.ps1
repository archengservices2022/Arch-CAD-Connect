<#
.SYNOPSIS
  Build, publish, COM-register (per-user, no admin) and manifest-install the
  Arch Engineering CAD Connect add-in for one or more Inventor versions.

.DESCRIPTION
  Steps:
    1. dotnet publish src/Arch.CadConnect.Inventor  (Release, framework-dependent)
       -> if this fails (e.g. Inventor is running and locking the output DLLs)
          the script STOPS immediately with a non-zero exit code. No COM
          registration and no .addin manifest changes are made.
    2. per-user COM registration of the generated comhost under
       HKCU\Software\Classes\CLSID\{ClassId}  (regsvr32 is NOT used, so no admin)
    3. render Manifest/Arch.CadConnect.addin.template with the absolute path of
       the published Arch.CadConnect.Inventor.dll and drop it into
       %APPDATA%\Autodesk\Inventor <version>\Addins\

  Re-run any time after a code change - it is idempotent.

  Windows PowerShell 5.1 compatible; pwsh is not required.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\install-addin.ps1

.EXAMPLE
  .\scripts\install-addin.ps1 -InventorVersions 2025,2026 -Configuration Release

.EXAMPLE
  .\scripts\install-addin.ps1 -SelfTest
  # Verifies the publish-failure fail-fast behavior without Inventor or dotnet.
#>
[CmdletBinding()]
param(
  [string[]] $InventorVersions,
  [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
  [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Project    = Join-Path $RepoRoot 'src/Arch.CadConnect.Inventor/Arch.CadConnect.Inventor.csproj'
$Template   = Join-Path $RepoRoot 'src/Arch.CadConnect.Inventor/Manifest/Arch.CadConnect.addin.template'
$PublishDir = Join-Path $RepoRoot ("build/output/{0}/net8.0-windows" -f $Configuration)
$ClassId    = '93C45016-BDCA-4734-BB1A-4A06C36E900C'
$ProgId     = 'Arch.CadConnect.Inventor.ArchAddInServer'

# Official product support target. Local detection may find other installs
# (e.g. 2023) and the script will still install for them for convenience, but
# they are NOT an expansion of the supported set.
$OfficialSupport = @('2025', '2026')

function Get-InstalledInventorVersions {
  Get-ChildItem 'C:\Program Files\Autodesk' -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^Inventor (20\d\d)$' } |
    ForEach-Object { $Matches[1] }
}

# Hard gate between step 1 and everything after it. A non-zero dotnet publish
# exit code MUST abort the installation - it must never fall through to COM
# registration / manifest install / a "complete" message. Throws on failure so
# the -SelfTest switch can exercise it directly; the caller turns that throw
# into a clean non-zero exit.
function Assert-PublishSucceeded {
  [CmdletBinding()]
  param([Parameter(Mandatory = $true)] [int] $ExitCode)

  if ($ExitCode -eq 0) { return }

  Write-Host ''
  Write-Host "ERROR: 'dotnet publish' failed with exit code $ExitCode." -ForegroundColor Red
  Write-Host 'The add-in was NOT installed. No COM registration was performed and no' -ForegroundColor Red
  Write-Host '.addin manifest was created or updated.' -ForegroundColor Red
  Write-Host ''
  Write-Host 'Most common cause: Inventor is still running and is holding a lock on the' -ForegroundColor Yellow
  Write-Host 'build output (Arch.CadConnect.Inventor.dll / .comhost.dll). Close every' -ForegroundColor Yellow
  Write-Host 'Inventor window, wait a few seconds, then run this script again.' -ForegroundColor Yellow
  Write-Host ''
  throw "dotnet publish failed (exit code $ExitCode) - installation aborted."
}

# --- self-test (no Inventor, no dotnet, no registry) ---------------------
if ($SelfTest) {
  Write-Host 'SelfTest: publish-failure fail-fast behavior'

  # success path must not throw
  Assert-PublishSucceeded -ExitCode 0

  # failure path MUST throw (which, uncaught, aborts the real run)
  $threw = $false
  try { Assert-PublishSucceeded -ExitCode 1 } catch { $threw = $true }
  if (-not $threw) {
    Write-Error 'SelfTest FAILED: a non-zero publish exit code did not abort.'
    exit 1
  }

  $threw = $false
  try { Assert-PublishSucceeded -ExitCode 255 } catch { $threw = $true }
  if (-not $threw) {
    Write-Error 'SelfTest FAILED: exit code 255 did not abort.'
    exit 1
  }

  Write-Host 'SelfTest PASSED: a failed dotnet publish aborts before COM registration / manifest install.'
  exit 0
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
& dotnet publish $Project -c $Configuration -o $PublishDir --nologo
$publishExitCode = $LASTEXITCODE
try {
  Assert-PublishSucceeded -ExitCode $publishExitCode
}
catch {
  # Assert-PublishSucceeded has already printed the actionable guidance.
  # Terminate the installer here with a non-zero exit code - nothing below
  # this point (COM registration, manifest install, completion message) runs.
  exit 1
}

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

Write-Host "`nInstallation complete for Inventor $($InventorVersions -join ', ')."
Write-Host 'Start a supported Inventor version and verify the ARCH ENGINEERING ribbon.'

$unsupported = @($InventorVersions | Where-Object { $OfficialSupport -notcontains $_ })
if ($unsupported.Count -gt 0) {
  Write-Host ''
  Write-Host ("Note: the officially supported Inventor versions for this add-in are {0}." -f ($OfficialSupport -join ' and ')) -ForegroundColor Yellow
  Write-Host ("Detected target(s) {0} were installed for local testing convenience only." -f ($unsupported -join ', ')) -ForegroundColor Yellow
}

Write-Host ''
Write-Host "If the ribbon does not appear: Tools > Add-Ins, check 'Arch Engineering CAD Connect', set 'Loaded/Unloaded' + 'Load Automatically'."

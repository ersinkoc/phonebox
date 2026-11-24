<#
.SYNOPSIS
  Builds PhoneBox-<version>-win-x64-setup.exe from a published PhoneBoxAgent.exe with Inno Setup (packaging\windows\PhoneBox.iss).
.EXAMPLE
  dotnet publish agent/PhoneBox.Agent -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
  .\tools\make-windows-installer.ps1 -Exe out\PhoneBoxAgent.exe -Version 0.9.28 -OutDir dist
.NOTES
  Needs Inno Setup 6.3 or later: winget install JRSoftware.InnoSetup (or choco install innosetup).
  THIRD-PARTY-NOTICES.txt is taken from the folder of the exe, where dotnet publish puts it.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutDir = 'dist'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 was not found: winget install JRSoftware.InnoSetup (or choco install innosetup).' }

$exePath = (Resolve-Path -LiteralPath $Exe).Path
$notices = Join-Path (Split-Path $exePath) 'THIRD-PARTY-NOTICES.txt'
if (-not (Test-Path -LiteralPath $notices)) { throw "THIRD-PARTY-NOTICES.txt is missing next to $exePath" }
# The only loose DLL next to the self-contained single-file agent is aspnetcorev2_inprocess.dll, the ASP.NET Core module for
# hosting under IIS; the agent runs on Kestrel and never loads it, which test-windows-installer.ps1 shows by running the
# installed copy without it. Any other DLL would be something the agent needs: stop here instead of leaving it out.
$loose = Get-ChildItem -LiteralPath (Split-Path $exePath) -Filter *.dll -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'aspnetcorev2_inprocess.dll' }
if ($loose) { throw "Native libraries next to $exePath ($($loose.Name -join ', ')): add them to [Files] in packaging\windows\PhoneBox.iss." }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = (Resolve-Path -LiteralPath $OutDir).Path

& $iscc "/DAppVersion=$Version" "/DSourceExe=$exePath" "/DSourceNotices=$notices" "/DOutDir=$out" /Qp (Join-Path $root 'packaging\windows\PhoneBox.iss')
if ($LASTEXITCODE) { throw "Inno Setup failed ($LASTEXITCODE)" }

$setup = Join-Path $out "PhoneBox-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "$setup was not written" }
Write-Host $setup

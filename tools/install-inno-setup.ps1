<#
.SYNOPSIS
  Installs the pinned Inno Setup (the tool that builds the Windows installer) and checks which version is there.
.NOTES
  The one place the build tool's version is chosen: an unpinned tool in the release pipeline could change what ships without
  any change in this repository. Used by build.yml and release.yml; change $Version here, and let the next run test it.
#>
param([string]$Version = '6.7.1')
$ErrorActionPreference = 'Stop'

# upgrade installs it when it is missing and moves a different version (the runner image has one) to the pinned one
choco upgrade innosetup --version=$Version --allow-downgrade --no-progress -y
if ($LASTEXITCODE) { throw "choco could not install Inno Setup $Version ($LASTEXITCODE)" }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe was not found after installing Inno Setup $Version" }
$info = (Get-Item -LiteralPath $iscc).VersionInfo
if ($info.ProductVersion -notlike "$Version*" -and $info.FileVersion -notlike "$Version*") {
    throw "Inno Setup $($info.ProductVersion) ($($info.FileVersion)) is installed, not the pinned $Version"
}
Write-Host "Inno Setup ${Version}: $iscc"

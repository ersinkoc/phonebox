<#
.SYNOPSIS
  Installs PhoneBox-<version>-win-x64-setup.exe silently, checks the result, runs the installed agent, upgrades over it and
  uninstalls. It changes the current user's real PhoneBox install (%LOCALAPPDATA%\PhoneBox\app), so it is for CI runners and
  throwaway machines. Settings (%APPDATA%\PhoneBox) are not touched.
#>
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$setup = (Resolve-Path -LiteralPath $Setup).Path
$app = Join-Path $env:LOCALAPPDATA 'PhoneBox\app'
$exe = Join-Path $app 'PhoneBoxAgent.exe'
$programs = Join-Path ([Environment]::GetFolderPath('Programs')) 'PhoneBox.lnk'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'PhoneBox.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{F4854D0A-0000-4AB6-A333-D4E0280DCB74}_is1'

function Assert([bool]$ok, [string]$what) { if (-not $ok) { throw "FAILED: $what" } ; Write-Host "ok  $what" }
function Install([string]$tasks) {
    $log = Join-Path $env:TEMP 'phonebox-setup.log'
    $p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/TASKS=$tasks", "/LOG=$log" -Wait -PassThru
    # what Setup decided about the autostart task, in the CI log (it is the one thing that depends on Setup's own rules)
    if (Test-Path -LiteralPath $log) { Select-String -Path $log -Pattern 'autostart' | ForEach-Object { Write-Host "    setup log: $($_.Line.Trim())" } }
    Assert ($p.ExitCode -eq 0) "installer exit code $($p.ExitCode) is 0"
}

Get-Process PhoneBoxAgent -ErrorAction SilentlyContinue | Stop-Process -Force
Install 'autostart'
Assert (Test-Path -LiteralPath $exe) 'PhoneBoxAgent.exe is in %LOCALAPPDATA%\PhoneBox\app'
Assert (Test-Path -LiteralPath (Join-Path $app 'THIRD-PARTY-NOTICES.txt')) 'the third-party notices are installed'
Assert (Test-Path -LiteralPath $programs) 'the Start Menu shortcut exists'
Assert (Test-Path -LiteralPath $startup) 'the Start-with-Windows shortcut exists (task chosen)'
Assert (Test-Path $uninstallKey) 'the uninstall entry is registered for the current user'
Assert ((Get-Item $exe).VersionInfo.FileVersion -like "$Version*") "the installed agent is version $Version"

python (Join-Path $root 'tools\smoke-agent.py') $exe --version $Version
Assert ($LASTEXITCODE -eq 0) 'the installed agent starts and serves its API (smoke-agent.py)'

# an upgrade over the install with the agent running (the installer must stop it), saying nothing about tasks: the
# Start-with-Windows shortcut stays
$running = Start-Process $exe -WorkingDirectory $app -PassThru
Start-Sleep 3
Assert ($null -ne (Get-Process -Id $running.Id -ErrorAction SilentlyContinue)) 'the agent is running before the upgrade (so the stop is really tested)'
Install ''
Assert (Test-Path -LiteralPath $exe) 'the upgrade left the agent in place'
Assert (-not (Get-Process -Id $running.Id -ErrorAction SilentlyContinue)) 'the upgrade stopped the running agent'
Assert (Test-Path -LiteralPath $startup) 'an upgrade that names no tasks keeps the Start-with-Windows shortcut'

# and explicitly unticking the task turns it off
Install '!autostart'
Assert (-not (Test-Path -LiteralPath $startup)) 'unticking the autostart task on an upgrade removes the Start-with-Windows shortcut'

# another folder is refused (uninstalling deletes the app folder, so /DIR must not be able to point at the settings)
$other = Join-Path $env:TEMP 'phonebox-other-dir'
$refused = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$other" -Wait -PassThru
Assert ($refused.ExitCode -ne 0) "installing into another folder is refused (exit code $($refused.ExitCode))"
Assert (-not (Test-Path -LiteralPath (Join-Path $other 'PhoneBoxAgent.exe'))) 'nothing was installed into the other folder'

# uninstall: the uninstaller copies itself to a temp folder and returns at once, so wait for the folder to go
$unins = Get-ChildItem (Join-Path $app 'unins*.exe') | Select-Object -First 1
Assert ($null -ne $unins) 'the uninstaller is installed'
Start-Process $unins.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
for ($i = 0; $i -lt 60 -and (Test-Path -LiteralPath $app); $i++) { Start-Sleep 1 }
Assert (-not (Test-Path -LiteralPath $app)) 'uninstalling removes the app folder'
Assert (-not (Test-Path -LiteralPath $programs)) 'uninstalling removes the Start Menu shortcut'
Assert (-not (Test-Path -LiteralPath $startup)) 'uninstalling removes the Start-with-Windows shortcut'
Assert (-not (Test-Path $uninstallKey)) 'uninstalling removes the uninstall entry'
Write-Host 'PASS: install, smoke run, upgrade, uninstall'

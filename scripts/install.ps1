<#
.SYNOPSIS
  Installs (or updates) PhoneBox on this computer and, optionally, on the connected phone.

.DESCRIPTION
  1. Publishes the agent to a temporary staging folder.
  2. Stops a running agent and installs the staged build in %LOCALAPPDATA%\PhoneBox\app.
  3. Optionally adds a Start-with-Windows shortcut.
  4. Optionally installs the panel APK on the connected phone: the newest of dist\ and the last build, rebuilt
     first when the app's sources are newer (or with -BuildApk).
  5. Starts the agent (it lives in the system tray).

.EXAMPLE
  .\scripts\install.ps1                 # agent only
  .\scripts\install.ps1 -Apk -Autostart # agent + phone app + start with Windows
#>
param(
    [switch]$Apk,        # also install the Android panel on the connected phone
    [switch]$BuildApk,   # with -Apk: always build the APK first
    [switch]$Autostart,  # start the agent with Windows
    [string]$Serial      # phone serial when several devices are connected
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'install-apk.ps1')
$app = Join-Path $env:LOCALAPPDATA 'PhoneBox\app'
$exe = Join-Path $app 'PhoneBoxAgent.exe'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required: winget install Microsoft.DotNet.SDK.10' }
$stage = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-publish-' + [guid]::NewGuid().ToString('N'))
try {
    Write-Host '==> Publishing agent'
    dotnet publish (Join-Path $root 'agent\PhoneBox.Agent') -c Release -f net10.0-windows10.0.19041.0 -o $stage --nologo -v q --disable-build-servers
    if ($LASTEXITCODE) { throw "dotnet publish failed (exit code $LASTEXITCODE); the installed agent was not updated" }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'PhoneBoxAgent.exe') -PathType Leaf)) {
        throw 'publish failed: staged agent executable not found'
    }

    Write-Host '==> Stopping running agent'
    Get-Process PhoneBoxAgent, DeskAgent -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    Write-Host "==> Installing to $app"
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    foreach ($entry in (Get-ChildItem -LiteralPath $stage -Force)) {
        Copy-Item -LiteralPath $entry.FullName -Destination $app -Recurse -Force
    }
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# Leftover shortcut from the pre-PhoneBox "DeskAgent" prototype.
$startup = [Environment]::GetFolderPath('Startup')
Remove-Item (Join-Path $startup 'DeskAgent.lnk') -ErrorAction SilentlyContinue

$programs = [Environment]::GetFolderPath('Programs')
if ($programs -and (Test-Path $programs)) {
    Write-Host '==> Adding Start Menu shortcut'
    # a shortcut that cannot be written must not stop the script before the agent is started again
    try {
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut((Join-Path $programs 'PhoneBox.lnk'))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $app
        $lnk.Description = 'PhoneBox agent'
        $lnk.Save()
    } catch {
        Write-Warning "Could not create the Start Menu shortcut: $($_.Exception.Message)"
    }
}

if ($Autostart) {
    Write-Host '==> Adding Start-with-Windows shortcut'
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path $startup 'PhoneBox.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $app
    $lnk.Description = 'PhoneBox agent'
    $lnk.Save()
}

Write-Host '==> Starting agent'
# A failed start must not skip the phone app below, but it is still a failure: it is raised once that step ran.
# A slow answer from a started agent is only a warning.
$launchError = $null
try { Start-Process $exe -WorkingDirectory $app } catch { $launchError = $_ }
if (-not $launchError) {
    Start-Sleep 2
    try { Invoke-RestMethod http://127.0.0.1:8765/api/info -TimeoutSec 5 | Out-Null; Write-Host 'PhoneBox is running. Settings: http://127.0.0.1:8765  (or the tray icon)' }
    catch { Write-Warning "The agent did not respond; see $env:LOCALAPPDATA\PhoneBox\agent.log" }
}

if ($Apk) {
    $adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
    if (-not $adb) {
        $adb = @("$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe") +
            (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Directory -Filter 'Google.PlatformTools*' -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName 'platform-tools\adb.exe' }) |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $adb) { throw 'adb not found: winget install Google.PlatformTools' }

    $built = Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk'
    $apkFile = @(Get-ChildItem (Join-Path $root 'dist') -Filter 'PhoneBox-*.apk' -ErrorAction SilentlyContinue) +
        @(Get-Item $built -ErrorAction SilentlyContinue) | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (Test-Path (Join-Path $root 'android\gradlew.bat')) {
        # in a source checkout, an APK from an older release must not win over newer app sources
        $newestSource = Get-ChildItem (Join-Path $root 'android\app\src') -Recurse -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($BuildApk -or -not $apkFile -or $newestSource.LastWriteTime -gt $apkFile.LastWriteTime) {
            Write-Host '==> Building the APK'
            Push-Location (Join-Path $root 'android')
            try { .\gradlew.bat assembleRelease --console=plain -q; if ($LASTEXITCODE) { throw "the Android build failed (exit code $LASTEXITCODE); the phone app was not updated" } } finally { Pop-Location }
            $apkFile = Get-Item $built
        }
    }
    if (-not $apkFile) { throw 'No APK found in dist\ and no Android sources to build one' }
    if (-not $Serial) {
        $devices = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match "`tdevice$" } | ForEach-Object { ($_ -split "`t")[0] })
        if ($devices.Count -eq 0) { throw 'No authorised Android device found (check the USB debugging prompt on the phone).' }
        if ($devices.Count -gt 1) { throw "Several devices are connected; pick one with -Serial: $($devices -join ', ')" }
        $Serial = $devices[0]
    }
    Write-Host "==> Installing $($apkFile.Name) on $Serial"
    $target = @('-s', $Serial)
    $out = Install-PhoneBoxApk -Adb $adb -ApkPath $apkFile.FullName -Serial $Serial
    $out | Select-Object -Last 1
    & $adb @target shell am start -n net.oxog.phonebox.deskpanel/.MainActivity | Out-Null
}

# the agent could not be started (see above): the phone step ran, the install still failed
if ($launchError) { throw $launchError }

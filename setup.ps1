<#
.SYNOPSIS
  PhoneBox setup for Windows: installs the latest release, everything it needs, and the phone app.

.DESCRIPTION
  Run it from a normal PowerShell window (not "Run as administrator"):

    irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex

  With options:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1))) -NoPhone

  What it does, asking first before anything outside the PhoneBox folders:
    1. Checks this PC (Windows 10/11, x64) and finds the latest PhoneBox release.
    2. Downloads the agent and checks its SHA-256 checksum (and its build provenance when the GitHub CLI is signed in).
    3. Installs adb (Android platform tools) when it is missing: with winget, or from Google's download.
    4. Installs the agent to %LOCALAPPDATA%\PhoneBox\app. Settings in %APPDATA%\PhoneBox are kept.
    5. Starts PhoneBox with Windows, puts the app on a phone connected over USB, starts the agent and opens its settings.

  Running it again updates PhoneBox or repairs the installation.

.PARAMETER Yes
  Answer every question with its default: for unattended runs.
.PARAMETER NoPhone
  Do not install the app on a phone over USB (for example when the phone will use Wi-Fi only).
.PARAMETER Serial
  The phone to use when several are connected (see "adb devices").
.PARAMETER NoAutostart
  Do not start PhoneBox with Windows.
.PARAMETER NoStart
  Install only; do not start the agent.
.PARAMETER Version
  Install this release (for example 0.9.8) instead of the latest.
.PARAMETER DryRun
  Download and check everything, show what would happen, and change nothing.
.PARAMETER AllowAdministrator
  Run even as administrator: only for PCs where every window runs elevated (UAC turned off) and for CI.
#>
param(
    [switch]$Yes,
    [switch]$NoPhone,
    [string]$Serial,
    [switch]$NoAutostart,
    [switch]$NoStart,
    [string]$Version,
    [switch]$DryRun,
    [switch]$AllowAdministrator
)

$Repo = 'ersinkoc/phonebox'
$Package = 'net.oxog.phonebox.deskpanel'
$SettingsUrl = 'http://127.0.0.1:8765'

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Info([string]$Text) { Write-Host "    $Text" }
function Write-Ok([string]$Text) { Write-Host "    OK  $Text" -ForegroundColor Green }
function Write-Caution([string]$Text) { Write-Host "    !   $Text" -ForegroundColor Yellow }

function Test-Interactive {
    if ($Yes) { return $false }
    try { return [Environment]::UserInteractive -and -not [Console]::IsInputRedirected } catch { return $false }
}

# Asks a yes/no question; with -Yes or without a console, the default answer is taken.
function Read-YesNo([string]$Question, [bool]$Default = $true) {
    $hint = if ($Default) { '[Y/n]' } else { '[y/N]' }
    if (-not (Test-Interactive)) {
        Write-Info "$Question $hint -> $(if ($Default) { 'yes' } else { 'no' }) (default)"
        return $Default
    }
    while ($true) {
        $answer = "$(Read-Host "    $Question $hint")".Trim().ToLowerInvariant()
        if ($answer -eq '') { return $Default }
        if ($answer -eq 'y' -or $answer -eq 'yes') { return $true }
        if ($answer -eq 'n' -or $answer -eq 'no') { return $false }
    }
}

# Runs a native program without letting Windows PowerShell 5.1 turn its stderr lines into errors.
function Invoke-Native([string]$File, [string[]]$Arguments) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $File @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    [pscustomobject]@{ Code = $code; Output = @($output) }
}

function Get-Download([string]$Url, [string]$Path) {
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path -Headers @{ 'User-Agent' = 'phonebox-setup' }
}

function Get-LatestVersion {
    try {
        $release = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$Repo/releases/latest" `
            -Headers @{ 'User-Agent' = 'phonebox-setup'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 30
        if ($release.tag_name) { return [string]$release.tag_name }
    } catch { }
    # The API allows 60 requests an hour per address; the release page redirects to the newest tag as well.
    $request = [System.Net.HttpWebRequest]::Create("https://github.com/$Repo/releases/latest")
    $request.AllowAutoRedirect = $false
    $request.Method = 'HEAD'
    $request.UserAgent = 'phonebox-setup'
    $response = $request.GetResponse()
    try { $location = [string]$response.Headers['Location'] } finally { $response.Close() }
    if ($location -match '/tag/([^/?#]+)$') { return $Matches[1] }
    throw 'Could not find the latest release on GitHub. Check the internet connection, or pass -Version (for example -Version 0.9.8).'
}

function Assert-Checksum([string]$File, [string]$SumsFile) {
    $name = Split-Path $File -Leaf
    $pattern = '^[0-9a-fA-F]{64}\s+\*?' + [regex]::Escape($name) + '\s*$'
    $line = Get-Content -LiteralPath $SumsFile | Where-Object { $_ -match $pattern } | Select-Object -First 1
    if (-not $line) { throw "$name is not listed in the release checksums; the download was not used." }
    $expected = [regex]::Match($line, '^[0-9a-fA-F]{64}').Value
    $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        throw "$name does not match the release checksum (expected $expected, got $actual). The download was damaged or changed on the way; nothing was installed. Run setup again."
    }
    Write-Ok "$name matches the release checksum"
}

function Test-Provenance([string]$File) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $gh -or (Invoke-Native $gh.Source @('auth', 'status')).Code -ne 0) {
        Write-Info "Build provenance not checked (needs the GitHub CLI, signed in): gh attestation verify $(Split-Path $File -Leaf) --repo $Repo"
        return
    }
    # gh 2.92.0 and earlier send the GitHub token to the TUF mirrors during verification (GHSA-8xvp-7hj6-mcj9)
    $ghVersion = [regex]::Match("$((Invoke-Native $gh.Source @('--version')).Output)", 'gh version (\d+\.\d+\.\d+)').Groups[1].Value
    if (-not $ghVersion -or [version]$ghVersion -lt [version]'2.93.0') {
        Write-Caution "Build provenance not checked: GitHub CLI $(if ($ghVersion) { $ghVersion } else { '(unknown version)' }) has a security problem in"
        Write-Caution '"gh attestation" (it sends your GitHub token to another server). Update it to 2.93.0 or later.'
        return
    }
    $result = Invoke-Native $gh.Source @('attestation', 'verify', $File, '--repo', $Repo)
    if ($result.Code -eq 0) { Write-Ok "$(Split-Path $File -Leaf) was built by this repository's release workflow (GitHub attestation)" }
    elseif (($result.Output -join ' ') -match 'unknown command') { Write-Info 'Build provenance not checked: this GitHub CLI is too old for "gh attestation".' }
    else { throw "The build provenance of $(Split-Path $File -Leaf) could not be verified; nothing was installed.`n$($result.Output -join "`n")" }
}

function Find-Adb {
    $candidates = @()
    $command = Get-Command adb.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $candidates += $command.Source }
    $candidates += Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
    $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path $winget) {
        $candidates += Get-ChildItem $winget -Directory -Filter 'Google.PlatformTools*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'platform-tools\adb.exe' }
    }
    $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

function Install-Adb([string]$Temp) {
    Write-Info 'adb (Android SDK Platform-Tools) is Google software under the Android SDK terms:'
    Write-Info 'https://developer.android.com/studio/releases/platform-tools'
    $winget = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($winget) {
        Write-Info 'Installing it with winget (Google.PlatformTools) ...'
        $result = Invoke-Native $winget.Source @('install', '--id', 'Google.PlatformTools', '--exact', '--source', 'winget',
            '--accept-package-agreements', '--accept-source-agreements')
        $adb = Find-Adb
        if ($adb) { return $adb }
        Write-Caution "winget could not install it (exit code $($result.Code)); using Google's download instead."
    }
    $zip = Join-Path $Temp 'platform-tools.zip'
    Get-Download 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' $zip
    $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    New-Item -ItemType Directory -Force $sdk | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $sdk -Force
    Find-Adb
}

function Stop-PhoneBox {
    $running = @(Get-Process PhoneBoxAgent, DeskAgent -ErrorAction SilentlyContinue)
    foreach ($process in $running) {
        try { $process.Kill(); [void]$process.WaitForExit(10000) } catch { }
    }
    if ($running.Count) { Write-Info "Stopped the running agent ($($running.Count) process(es))" }
}

# Copies the new release over the installed one; Windows can hold a just-stopped program's files for a moment.
function Copy-Release([string]$From, [string]$To) {
    for ($attempt = 1; ; $attempt++) {
        try { Copy-Item (Join-Path $From '*') $To -Recurse -Force -ErrorAction Stop; return }
        catch {
            if ($attempt -ge 20) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Get-PortOwner([int]$Port) {
    try {
        $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1
        if ($listener) { return (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue).ProcessName }
    } catch { }
    $null
}

function Get-Devices([string]$Adb) {
    foreach ($line in (Invoke-Native $Adb @('devices')).Output) {
        # "<serial><TAB><state>"; the header and adb's "daemon started" lines have no tab
        if ($line -match "^(\S+)`t(.+)$") { [pscustomobject]@{ Serial = $Matches[1]; State = $Matches[2].Trim() } }
    }
}

function Show-PhoneHelp {
    Write-Info 'On the phone (Android 5.0 or later):'
    Write-Info '  1. Settings > About phone: tap "Build number" seven times to turn on Developer options.'
    Write-Info '  2. Settings > System > Developer options: turn on "USB debugging".'
    Write-Info '     Xiaomi / Redmi / POCO: also turn on "Install via USB" and "USB debugging (Security settings)".'
    Write-Info '  3. Connect it with a data cable (some cables only charge) and unlock the screen.'
    Write-Info '  4. Answer "Allow USB debugging?" with Allow; tick "Always allow from this computer".'
    Write-Info 'If the phone never shows up, Windows may need its USB driver: Google phones use the Google USB Driver,'
    Write-Info 'other brands have one on their support site (Device Manager shows the phone with a warning sign).'
}

# Waits for exactly one authorised phone; returns its serial, or $null to skip.
function Select-Phone([string]$Adb) {
    [void](Invoke-Native $Adb @('start-server'))
    $wanted = $Serial
    while ($true) {
        $devices = @(Get-Devices $Adb)
        if ($wanted) { $devices = @($devices | Where-Object { $_.Serial -eq $wanted }) }
        # only phones on a cable are picked automatically, not emulators or wireless-debugging connections
        $ready = @($devices | Where-Object { $_.State -eq 'device' -and ($wanted -or ($_.Serial -notmatch '^emulator-|:|\._tcp')) })
        if ($ready.Count -eq 1) { return $ready[0].Serial }
        if ($ready.Count -gt 1) {
            Write-Caution "Several phones are connected: $(($ready | ForEach-Object Serial) -join ', ')"
            if (-not (Test-Interactive)) { Write-Caution 'Run setup again with -Serial <id> to pick one; skipping the phone app.'; return $null }
            $choice = "$(Read-Host '    Type the serial of the phone to use (empty to skip)')".Trim()
            if (-not $choice) { return $null }
            $wanted = $choice
            continue
        }
        foreach ($device in $devices) {
            if ($device.State -eq 'unauthorized') { Write-Caution "$($device.Serial): waiting for you to allow USB debugging on the phone (unlock it and tap Allow)." }
            elseif ($device.State -eq 'offline') { Write-Caution "$($device.Serial): offline. Unplug it and plug it in again." }
            elseif ($device.State -eq 'device') { Write-Caution "$($device.Serial): an emulator or a wireless connection, not used unless you pass -Serial $($device.Serial)." }
            else { Write-Caution "$($device.Serial): $($device.State)" }
        }
        if (-not $devices.Count) {
            if ($wanted) { Write-Caution "No phone with the serial $wanted is connected." } else { Write-Caution 'No phone is connected over USB yet.' }
            Show-PhoneHelp
        }
        if (-not (Test-Interactive)) { Write-Caution 'Skipping the phone app (no phone ready).'; return $null }
        $answer = "$(Read-Host '    Press Enter to check again, or type S to skip the phone')".Trim().ToLowerInvariant()
        if ($answer -eq 's' -or $answer -eq 'skip') { return $null }
        [void](Invoke-Native $Adb @('reconnect', 'offline'))
    }
}

function Install-PhoneApp([string]$Adb, [string]$Apk, [string]$Device) {
    $sdkLevel = 0
    [void][int]::TryParse(("$((Invoke-Native $Adb @('-s', $Device, 'shell', 'getprop', 'ro.build.version.sdk')).Output)").Trim(), [ref]$sdkLevel)
    if ($sdkLevel -gt 0 -and $sdkLevel -lt 21) {
        Write-Caution "This phone runs Android API $sdkLevel; PhoneBox needs Android 5.0 (API 21) or later. The app was not installed."
        return $false
    }
    Write-Info "Installing $(Split-Path $Apk -Leaf) on $Device (the phone may ask you to confirm) ..."
    $result = Invoke-Native $Adb @('-s', $Device, 'install', '-r', $Apk)
    $text = $result.Output -join ' '
    if ($text -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
        Write-Caution 'The phone has a PhoneBox app signed with a different key (for example one you built yourself).'
        Write-Caution 'It was left as it is, with its data. To switch to the release app, uninstall PhoneBox on the phone'
        Write-Caution '(this deletes its settings) and run setup again.'
        return $false
    }
    if ($text -match 'INSTALL_FAILED_VERSION_DOWNGRADE') { Write-Ok 'The phone already has a newer PhoneBox app; it was kept.'; return $true }
    if ($text -match 'INSTALL_FAILED_USER_RESTRICTED|INSTALL_FAILED_ABORTED') {
        Write-Caution 'The phone refused the installation. Confirm it on the phone; Xiaomi / Redmi / POCO phones also need'
        Write-Caution '"Install via USB" in Developer options. Then run setup again.'
        return $false
    }
    if ($result.Code -ne 0 -or $text -notmatch 'Success') {
        Write-Caution "The app could not be installed: $text"
        return $false
    }
    Write-Ok 'PhoneBox app installed on the phone'
    [void](Invoke-Native $Adb @('-s', $Device, 'shell', 'am', 'start', '-n', "$Package/.MainActivity"))
    $true
}

function Install-PhoneBox {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads in Windows PowerShell 5.1 many times slower
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    Write-Host ''
    Write-Host 'PhoneBox setup' -ForegroundColor Cyan
    if ($DryRun) { Write-Host 'Dry run: downloads and checks everything, changes nothing.' -ForegroundColor Yellow }

    Write-Step 'Checking this PC'
    $architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    if ($architecture -eq 'ARM64') {
        throw "There is no PhoneBox build for Windows on ARM yet: the x64 build would run, but could not update itself. Please ask for it in https://github.com/$Repo/issues."
    }
    if ($architecture -ne 'AMD64') { throw "PhoneBox needs 64-bit Windows (this PC reports $architecture)." }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 19041) { Write-Caution "PhoneBox is built for Windows 10 version 2004 (build 19041) or later; this PC has build $build, so some parts may not work." }
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $AllowAdministrator) {
        throw 'Run setup from a normal PowerShell window, not "Run as administrator". PhoneBox runs as you and needs no administrator rights; as administrator it would start with full rights, or install for the administrator account instead of yours.'
    }
    $scoop = if ($env:SCOOP) { $env:SCOOP } else { Join-Path $HOME 'scoop' }
    if (Test-Path (Join-Path $scoop 'apps\phonebox')) {
        throw 'PhoneBox is installed with Scoop. Update it with "scoop update phonebox". To switch to this setup instead, run "scoop uninstall phonebox" first (your settings stay) and then setup again; two copies would fight over the same port.'
    }
    $app = Join-Path $env:LOCALAPPDATA 'PhoneBox\app'
    $exe = Join-Path $app 'PhoneBoxAgent.exe'
    $installed = $null
    if (Test-Path $exe) { $installed = ("$((Get-Item $exe).VersionInfo.ProductVersion)" -split '\+')[0] }
    Write-Ok "Windows build $build, x64$(if ($installed) { ", PhoneBox $installed installed" })"
    # Smart App Control (Windows 11) blocks programs that are neither signed nor known to Microsoft: 1 on, 2 evaluation
    $smartAppControl = 0
    try { $smartAppControl = [int](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction Stop).VerifiedAndReputablePolicyState } catch { }
    if ($smartAppControl -eq 1) {
        Write-Caution 'Smart App Control is on. It blocks programs that are neither signed nor known to Microsoft, and'
        Write-Caution 'PhoneBoxAgent.exe is not code-signed yet (signing through SignPath Foundation is pending:'
        Write-Caution "https://github.com/$Repo/issues/56), so Windows will probably refuse to start it. Setup still installs it."
        Write-Caution 'Please keep Smart App Control on: on older Windows builds it cannot be turned back on without resetting Windows.'
    } elseif ($smartAppControl -eq 2) {
        Write-Caution 'Smart App Control is in evaluation mode: it blocks nothing yet. If Windows turns it on later, it will block'
        Write-Caution "PhoneBoxAgent.exe until the agent is code-signed (pending: https://github.com/$Repo/issues/56)."
    }

    Write-Step 'Finding the release'
    $tag = if ($Version) { $Version } else { Get-LatestVersion }
    $ver = $tag.Trim().TrimStart('v')
    if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected version '$tag'." }
    $base = "https://github.com/$Repo/releases/download/v$ver"
    if ($installed -eq $ver) { Write-Ok "PhoneBox $ver (already installed; it will be reinstalled, settings stay)" }
    elseif ($installed) { Write-Ok "PhoneBox $ver (updating from $installed; settings stay)" }
    else { Write-Ok "PhoneBox $ver" }

    $temp = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-setup-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $temp | Out-Null
    try {
        Write-Step "Downloading PhoneBox $ver"
        $zipName = "PhoneBoxAgent-$ver-win-x64.zip"
        $sums = Join-Path $temp "SHA256SUMS-$ver.txt"
        $zip = Join-Path $temp $zipName
        try { Get-Download "$base/SHA256SUMS-$ver.txt" $sums } catch { throw "Release $ver was not found ($base). $($_.Exception.Message)" }
        Write-Info "$zipName (about 65 MB) ..."
        Get-Download "$base/$zipName" $zip
        Assert-Checksum $zip $sums
        Test-Provenance $zip
        Write-Info 'The agent is not code-signed yet (the SignPath Foundation application is pending). Windows SmartScreen'
        Write-Info 'or an antivirus can warn about new unsigned programs; the checksum above shows that this is the file'
        Write-Info "GitHub built from the public source. Details: https://github.com/$Repo/issues/69"

        $extract = Join-Path $temp 'agent'
        Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
        Get-ChildItem $extract -Recurse -File | Unblock-File
        if (-not (Test-Path (Join-Path $extract 'PhoneBoxAgent.exe'))) {
            throw "PhoneBoxAgent.exe is missing from the download: an antivirus probably removed it. See https://github.com/$Repo/issues/69 and restore it from quarantine, or add an exclusion, then run setup again."
        }

        Write-Step 'Checking adb (needed for the USB connection to the phone)'
        $adb = Find-Adb
        if ($adb) { Write-Ok $adb }
        else {
            Write-Caution 'adb is not installed. Without it PhoneBox still works over Wi-Fi, but cannot reach the phone over USB'
            Write-Caution '(wake it on plug-in, install and update the phone app, fall back to USB when Wi-Fi drops).'
            if ($DryRun) { Write-Info 'Would offer to install it.' }
            elseif (Read-YesNo 'Install adb now?') {
                $adb = Install-Adb $temp
                if ($adb) { Write-Ok "adb installed: $adb" } else { Write-Caution 'adb could not be installed; PhoneBox will work over Wi-Fi only until it is.' }
            }
        }

        $owner = Get-PortOwner 8765
        if ($owner -and $owner -ne 'PhoneBoxAgent' -and $owner -ne 'DeskAgent') {
            Write-Caution "Port 8765 is used by $owner. PhoneBox needs this port; close $owner or PhoneBox will not start."
        }

        if ($DryRun) {
            Write-Step 'Dry run finished'
            Write-Info "Would stop the running agent and install PhoneBox $ver to $app (settings in $env:APPDATA\PhoneBox stay)."
            if (-not $NoAutostart) { Write-Info 'Would ask to start PhoneBox with Windows.' }
            if (-not $NoPhone) { Write-Info 'Would offer to install the app on a phone connected over USB.' }
            Write-Info 'Nothing was changed.'
            return
        }

        Write-Step "Installing to $app"
        Stop-PhoneBox
        # The files are replaced in place: the folder itself cannot be renamed while another program has it as its
        # working directory, such as the adb server the agent started.
        $old = "$app.old"
        Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $app) { Copy-Item $app $old -Recurse -Force }   # to put back if the copy fails
        try {
            New-Item -ItemType Directory -Force $app | Out-Null
            Copy-Release $extract $app
            # files the new version no longer has, for example the DLLs of a framework-dependent (source) install
            $keep = @{}
            Get-ChildItem $extract -Recurse -File | ForEach-Object { $keep[$_.FullName.Substring($extract.Length)] = $true }
            Get-ChildItem $app -Recurse -File | Where-Object { -not $keep[$_.FullName.Substring($app.Length)] } |
                Remove-Item -Force -ErrorAction SilentlyContinue
        } catch {
            if (Test-Path $old) { try { Copy-Item (Join-Path $old '*') $app -Recurse -Force } catch { } }
            throw "Installing failed and the previous version was put back: $($_.Exception.Message). Close PhoneBox (tray icon > Exit) and run setup again."
        }
        Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
        Write-Ok "PhoneBox $ver installed; settings are in $env:APPDATA\PhoneBox"

        $startup = [Environment]::GetFolderPath('Startup')
        Remove-Item (Join-Path $startup 'DeskAgent.lnk') -ErrorAction SilentlyContinue   # the pre-PhoneBox prototype
        $shortcut = Join-Path $startup 'PhoneBox.lnk'
        if ($NoAutostart) {
            if (Test-Path $shortcut) {
                Remove-Item $shortcut -Force
                Write-Info 'Removed the Start-with-Windows shortcut (-NoAutostart).'
            }
        } else {
            Write-Step 'Start with Windows'
            if (Read-YesNo 'Start PhoneBox when you sign in to Windows?') {
                $shell = New-Object -ComObject WScript.Shell
                $link = $shell.CreateShortcut($shortcut)
                $link.TargetPath = $exe
                $link.WorkingDirectory = $app
                $link.Description = 'PhoneBox agent'
                $link.Save()
                Write-Ok 'Added to the Startup folder (turn it off in Task Manager > Startup apps)'
            } elseif (Test-Path $shortcut) {
                Remove-Item $shortcut -Force
                Write-Info 'Removed the existing Start-with-Windows shortcut.'
            }
        }

        $phoneDone = $false
        if (-not $NoPhone) {
            Write-Step 'Phone app'
            if (-not $adb) {
                Write-Caution 'Skipped: adb is not installed.'
            } elseif (Read-YesNo 'Install the PhoneBox app on a phone connected over USB now?') {
                Write-Info 'USB debugging lets an allowed computer control the phone. Allow only your own computers; you can'
                Write-Info 'revoke them under Developer options > "Revoke USB debugging authorisations".'
                $device = Select-Phone $adb
                if ($device) {
                    # The phone app is optional: a failed download or checksum skips it, and the agent (already
                    # installed and stopped for the update) still starts below.
                    try {
                        $apk = Join-Path $temp "PhoneBox-$ver.apk"
                        Get-Download "$base/PhoneBox-$ver.apk" $apk
                        Assert-Checksum $apk $sums
                        $phoneDone = Install-PhoneApp $adb $apk $device
                    } catch {
                        Write-Caution "Phone app skipped: $($_.Exception.Message)"
                    }
                }
            }
        }

        if ($NoStart) {
            Write-Step 'Done (not started)'
            Write-Info "Start it with $exe"
        } else {
            Write-Step 'Starting PhoneBox'
            $blocked = $false
            try {
                Start-Process -FilePath $exe -WorkingDirectory $app -ErrorAction Stop
            } catch {
                # Smart App Control and other App Control / AppLocker policies refuse the file (Win32 errors 4551 and 1260).
                # Start-Process keeps only the message, in the system language, so compare it with the system's own text.
                $why = "$($_.Exception.Message)"
                $says = { param($code) $text = ([ComponentModel.Win32Exception]::new($code).Message).Trim().TrimEnd('.'); $text -and $why.Contains($text) }
                $appControl = & $says 4551   # App Control for Business, which Smart App Control uses
                $groupPolicy = & $says 1260  # AppLocker or a software restriction policy
                if (-not ($appControl -or $groupPolicy)) { throw }
                $blocked = $true
                Write-Caution "Windows did not let PhoneBoxAgent.exe start: $why"
                if ($appControl -and $smartAppControl -eq 1) {
                    Write-Caution 'PhoneBox is installed, but Smart App Control will not run it until the agent is code-signed.'
                    Write-Caution "Signing is pending: https://github.com/$Repo/issues/56. Please keep Smart App Control on."
                } else {
                    Write-Caution 'PhoneBox is installed, but a policy on this computer does not allow it to run. On a work or school'
                    Write-Caution 'computer, ask its administrator to allow PhoneBoxAgent.exe.'
                }
            }
            $up = $false
            for ($i = 0; $i -lt 30 -and -not $up -and -not $blocked; $i++) {
                Start-Sleep -Milliseconds 700
                try { Invoke-RestMethod -UseBasicParsing "$SettingsUrl/api/info" -TimeoutSec 2 | Out-Null; $up = $true } catch { }
            }
            if ($up) {
                Write-Ok "PhoneBox is running: settings at $SettingsUrl (also from the tray icon)"
                try { Start-Process $SettingsUrl } catch { }
            } elseif ($blocked) {
                # explained above
            } elseif (-not (Test-Path $exe)) {
                Write-Caution "PhoneBoxAgent.exe was removed, probably by an antivirus. See https://github.com/$Repo/issues/69."
            } else {
                Write-Caution "The agent did not answer. Its log: $env:LOCALAPPDATA\PhoneBox\agent.log"
                if ($smartAppControl -eq 1) {
                    Write-Caution 'Smart App Control may have blocked it: see Windows Security > App & browser control, and the warning above.'
                }
            }
        }

        Write-Step 'Good to know'
        if ($phoneDone) { Write-Info 'Keep the phone plugged in: PhoneBox wakes it and opens the panel.' }
        elseif (-not $NoPhone) {
            Write-Info "Phone app later: plug the phone in and use Settings > General > Install the phone app, or run setup again."
        }
        Write-Info 'Wi-Fi only: install the app from the release page on the phone, then on the computer open'
        Write-Info 'Settings > General > Wi-Fi & Tailscale > Pair a phone over Wi-Fi and scan the QR code.'
        Write-Info 'When you turn Wi-Fi on, Windows asks to let PhoneBox through the firewall: allow Private networks only.'
        Write-Info "Until then PhoneBox only listens on this computer. Every connection it makes: https://github.com/$Repo/blob/main/PRIVACY.md"
        Write-Info 'Updates: PhoneBox updates itself from Settings. Run setup again any time to repair it.'
        Write-Info "Uninstall: & ([scriptblock]::Create((irm https://raw.githubusercontent.com/$Repo/main/scripts/uninstall.ps1))) [-Purge] [-Apk]"
    } finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Everything runs from here, so a download cut off half way cannot run half a script.
try {
    Install-PhoneBox
    $global:LASTEXITCODE = 0   # optional native checks (gh, winget) may have left a non-zero code; setup succeeded
} catch {
    Write-Host ''
    Write-Host "Setup stopped: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Help: https://github.com/$Repo#quick-start  -  issues: https://github.com/$Repo/issues" -ForegroundColor Red
    if (-not (Test-Interactive)) { throw }   # unattended runs must fail visibly
}

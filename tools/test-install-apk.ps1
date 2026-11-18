$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/install-apk.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-installer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$fakeAdb = Join-Path $fixture 'adb.ps1'
$env:PHONEBOX_INSTALL_TEST_LOG = Join-Path $fixture 'calls.txt'
@'
Add-Content -LiteralPath $env:PHONEBOX_INSTALL_TEST_LOG -Value ($args -join ' ')
if ($args -contains 'uninstall') { Write-Output 'Success'; exit 0 }
if ($env:PHONEBOX_INSTALL_TEST_MODE -eq 'signature') { Write-Output 'Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]'; exit 1 }
if ($env:PHONEBOX_INSTALL_TEST_MODE -eq 'failure') { Write-Output 'Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]'; exit 1 }
Write-Output 'Success'; exit 0
'@ | Set-Content -LiteralPath $fakeAdb -Encoding utf8
try {
    foreach ($mode in @('signature', 'failure')) {
        $env:PHONEBOX_INSTALL_TEST_MODE = $mode
        $failed = $false
        try { Install-PhoneBoxApk -Adb $fakeAdb -ApkPath 'test.apk' -Serial 'test-phone' | Out-Null } catch { $failed = $true }
        if (-not $failed) { throw "$mode install must report failure" }
        if ((Get-Content -LiteralPath $env:PHONEBOX_INSTALL_TEST_LOG) -match 'uninstall') { throw 'Installer must never remove the existing app automatically' }
    }
    $env:PHONEBOX_INSTALL_TEST_MODE = 'success'
    Install-PhoneBoxApk -Adb $fakeAdb -ApkPath 'test.apk' -Serial 'test-phone' | Out-Null

    # Regression check: scripts/install.ps1 starts the newly installed agent before the optional -Apk step, a failed
    # -Apk step can't leave it stopped, and a failed start doesn't skip the phone step but still fails the install.
    $installer = Join-Path $PSScriptRoot '../scripts/install.ps1'
    $adbMissing = 'adb not found: winget install Google.PlatformTools'
    $scenarios = @(
        @{ Name = 'start ok, -Apk'; LaunchFails = $false; Apk = $true; Expect = $adbMissing },
        @{ Name = 'start fails, -Apk'; LaunchFails = $true; Apk = $true; Expect = $adbMissing },
        @{ Name = 'start fails'; LaunchFails = $true; Apk = $false; Expect = 'blocked by policy' }
    )
    foreach ($case in $scenarios) {
        $prevLocal = $env:LOCALAPPDATA
        $prevPath = $env:PATH
        $appBox = Join-Path $fixture 'appbox'
        New-Item -ItemType Directory -Path $appBox | Out-Null
        $state = @{ Order = [System.Collections.Generic.List[string]]::new(); LaunchFails = $case.LaunchFails }
        $thrown = $null
        try {
            $env:LOCALAPPDATA = $appBox
            $env:PATH = $appBox
            function global:Get-Command {
                param([string]$Name, $ErrorAction)
                if ($Name -eq 'dotnet') { return [pscustomobject]@{ Name = 'dotnet'; Source = 'dotnet' } }
                if ($Name -eq 'adb') { $state.Order.Add('apk'); return $null }
                Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
            }
            function global:dotnet {
                $outIdx = [Array]::IndexOf($args, '-o')
                $stageDir = $args[$outIdx + 1]
                New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $stageDir 'PhoneBoxAgent.exe') -Value 'staged' -Encoding ascii
                $global:LASTEXITCODE = 0
            }
            function global:Get-Process { param([string[]]$Name, $ErrorAction) $state.Order.Add('stop'); return @() }
            function global:Start-Sleep { param($Seconds, $Milliseconds) }
            function global:Remove-Item {
                param($Path, $LiteralPath, [switch]$Recurse, [switch]$Force, $ErrorAction)
                $t = if ($LiteralPath) { $LiteralPath } else { $Path }
                if ($t -and "$t" -like '*DeskAgent.lnk') { return }
                Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
            }
            function global:Start-Process {
                param($FilePath, $WorkingDirectory)
                $state.Order.Add('start')
                if ($state.LaunchFails) { throw 'blocked by policy' }
            }
            function global:Invoke-RestMethod { param($Uri, $TimeoutSec) return @{} }
            function global:Write-Host { param($Object) }
            function global:Write-Warning { param($Message) }
            try { if ($case.Apk) { & $installer -Apk } else { & $installer } } catch { $thrown = $_.Exception.Message }
        } finally {
            $env:LOCALAPPDATA = $prevLocal
            $env:PATH = $prevPath
            foreach ($fn in 'Get-Command','dotnet','Get-Process','Start-Sleep','Remove-Item','Start-Process','Invoke-RestMethod','Write-Host','Write-Warning') {
                Microsoft.PowerShell.Management\Remove-Item "Function:\global:$fn" -ErrorAction SilentlyContinue
            }
            Microsoft.PowerShell.Management\Remove-Item -LiteralPath $appBox -Recurse -Force -ErrorAction SilentlyContinue
        }
        $order = $state.Order -join '->'
        $expectedOrder = if ($case.Apk) { 'stop->start->apk' } else { 'stop->start' }
        if ($order -ne $expectedOrder -or $thrown -cne $case.Expect) {
            throw "install.ps1 ($($case.Name)): EXPECTED $expectedOrder, error '$($case.Expect)' | ACTUAL $order, error '$thrown'"
        }
    }

    Write-Output 'PASS: signature mismatch and other failures preserve the installed app; successful updates work'
} finally {
    [IO.File]::Delete($fakeAdb)
    [IO.File]::Delete($env:PHONEBOX_INSTALL_TEST_LOG)
    [IO.Directory]::Delete($fixture)
    $env:PHONEBOX_INSTALL_TEST_LOG = $null
    $env:PHONEBOX_INSTALL_TEST_MODE = $null
}

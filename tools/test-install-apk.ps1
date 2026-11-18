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

    # Regression check: scripts/install.ps1 must start the newly installed agent BEFORE optional -Apk errors can abort
    $installer = Join-Path $PSScriptRoot '../scripts/install.ps1'
    $prevLocal = $env:LOCALAPPDATA
    $prevPath = $env:PATH
    $appBox = Join-Path $fixture 'appbox'
    New-Item -ItemType Directory -Path $appBox | Out-Null
    $state = @{ Order = [System.Collections.Generic.List[string]]::new() }
    $apkThrew = $false
    try {
        $env:LOCALAPPDATA = $appBox
        $env:PATH = $appBox
        function global:Get-Command {
            param([string]$Name, $ErrorAction)
            if ($Name -eq 'dotnet') { return [pscustomobject]@{ Name = 'dotnet'; Source = 'dotnet' } }
            if ($Name -eq 'adb') { return $null }
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
        function global:Start-Process { param($FilePath, $WorkingDirectory) $state.Order.Add('start') }
        function global:Invoke-RestMethod { return @{} }
        function global:Write-Host { param($Object) }
        try { & $installer -Apk } catch { $apkThrew = $true }
    } finally {
        $env:LOCALAPPDATA = $prevLocal
        $env:PATH = $prevPath
        foreach ($fn in 'Get-Command','dotnet','Get-Process','Start-Sleep','Remove-Item','Start-Process','Invoke-RestMethod','Write-Host') {
            Microsoft.PowerShell.Management\Remove-Item "Function:\global:$fn" -ErrorAction SilentlyContinue
        }
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $appBox -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (($state.Order -join '->') -ne 'stop->start' -or -not $apkThrew) {
        throw "install.ps1 -Apk failure left the installed agent stopped ($($state.Order -join '->'), threw=$apkThrew)"
    }

    Write-Output 'PASS: signature mismatch and other failures preserve the installed app; successful updates work'
} finally {
    [IO.File]::Delete($fakeAdb)
    [IO.File]::Delete($env:PHONEBOX_INSTALL_TEST_LOG)
    [IO.Directory]::Delete($fixture)
    $env:PHONEBOX_INSTALL_TEST_LOG = $null
    $env:PHONEBOX_INSTALL_TEST_MODE = $null
}

$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot '../setup.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($setup, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in 'Get-UsbInstallPolicy', 'Select-Phone') {
    $function = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}

function Write-Caution([string]$Text) {}
function Test-Interactive { return $false }
function Get-Devices([string]$Adb) {
    [pscustomobject]@{ Serial = 'reader'; State = 'device' }
    [pscustomobject]@{ Serial = 'panel'; State = 'device' }
}
function Invoke-Native([string]$File, [string[]]$Arguments) {
    if ($Arguments[0] -eq '--print-install-dir') {
        if ($mode -eq 'throws') { throw 'agent could not launch' }
        if ($mode -eq 'current') { [IO.File]::WriteAllLines($Arguments[2], @('reader')) }
        return [pscustomobject]@{ Code = if ($mode -eq 'error') { 1 } else { 0 } }
    }
    [pscustomobject]@{ Code = 0 }
}

$prev = $env:APPDATA
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-setup-policy-' + [guid]::NewGuid().ToString('N'))
try {
    $env:APPDATA = $fixture
    $configDir = New-Item -ItemType Directory -Path (Join-Path $fixture 'PhoneBox') -Force
    $mode = 'current'
    $policy = Get-UsbInstallPolicy 'fake-agent' (Join-Path $fixture 'policy')
    $Serial = ''
    if ((Select-Phone 'fake-adb' $policy.Ignored) -ne 'panel') { throw 'Setup must pick the non-ignored device.' }
    $Serial = 'reader'
    if (Select-Phone 'fake-adb' $policy.Ignored) { throw 'An explicit serial must respect the ignore settings.' }

    $mode = 'legacy'
    $legacyOutput = Join-Path $fixture 'legacy'
    if (-not (Get-UsbInstallPolicy 'fake-agent' $legacyOutput)) { throw 'A legacy agent and no config must permit setup.' }
    foreach ($config in '{"ignoredDeviceSerials":["reader"]}', '{"ignoredDevice\u0053erials":["reader"]}', '{') {
        Set-Content -LiteralPath (Join-Path $configDir.FullName 'config.json') -Value $config
        if (Get-UsbInstallPolicy 'fake-agent' $legacyOutput) { throw 'A legacy agent cannot validate an existing policy.' }
    }
    foreach ($mode in 'error', 'throws') {
        if (Get-UsbInstallPolicy 'fake-agent' $legacyOutput) { throw 'An unreadable policy must skip phone setup.' }
    }
    Write-Output 'PASS: the Windows setup respects the ignored USB list and handles older agents'
} finally {
    $env:APPDATA = $prev
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ((Test-Path -LiteralPath $fixture) -and (Resolve-Path -LiteralPath $fixture).Path.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}

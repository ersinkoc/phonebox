[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Get-Location) ("antigravity-diagnostics-{0:yyyyMMdd-HHmmss}.txt" -f (Get-Date)))
)

$ErrorActionPreference = 'SilentlyContinue'
$report = [System.Collections.Generic.List[string]]::new()

function Add-Report([string]$Text = '') {
    [void]$report.Add($Text)
}

function Format-Value([object]$Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return '<none>' }
    return [string]$Value
}

function Redact-CommandLine([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return '<not available>' }
    $redacted = $CommandLine
    $redacted = [regex]::Replace($redacted, '(?i)(--?(?:api[-_]?key|auth(?:entication)?|bearer|csrf(?:[-_]?token)?|credential|oauth|password|secret|token))(?:(\s+|=)[^\s]+)', '$1=<redacted>')
    $redacted = [regex]::Replace($redacted, '(?i)((?:api[-_]?key|authorization|cookie|csrf(?:[-_]?token)?|oauth|password|secret|token)\s*[:=]\s*)("[^"]*"|''[^'']*''|[^\s]+)', '$1<redacted>')
    return $redacted
}

function Get-FileVersion([string]$Path) {
    try {
        return [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    } catch {
        return $null
    }
}

function Invoke-Version([string]$Path) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $process.StartInfo.FileName = $Path
    $process.StartInfo.Arguments = '--version'
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    try {
        [void]$process.Start()
        if (-not $process.WaitForExit(3000)) {
            $process.Kill()
            return '<version command timed out>'
        }
        $output = ($process.StandardOutput.ReadToEnd() + ' ' + $process.StandardError.ReadToEnd()).Trim()
        if ($output.Length -gt 300) { $output = $output.Substring(0, 300) + '...' }
        return (Format-Value $output)
    } catch {
        return '<version command unavailable>'
    } finally {
        $process.Dispose()
    }
}

function Add-PathRecord([string]$Label, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Add-Report ("{0}: {1} | version={2} | length={3}" -f $Label, $Path, (Format-Value (Get-FileVersion $Path)), (Get-Item $Path).Length)
    } elseif (Test-Path -LiteralPath $Path -PathType Container) {
        Add-Report ("{0}: {1} | directory" -f $Label, $Path)
    }
}

Add-Report 'PhoneBox Antigravity diagnostic report'
Add-Report ("Generated: {0}" -f (Get-Date -Format o))
Add-Report 'Privacy: names, paths, metadata, and redacted process arguments only; no file contents or credentials are collected.'
Add-Report ''

Add-Report '[System]'
Add-Report ("Computer: {0}" -f $env:COMPUTERNAME)
Add-Report ("OS: {0}" -f (Get-CimInstance Win32_OperatingSystem).Caption)
Add-Report ("PowerShell: {0}" -f $PSVersionTable.PSVersion)
Add-Report ''

$candidateInstallations = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Antigravity'),
    (Join-Path $env:LOCALAPPDATA 'Antigravity'),
    (Join-Path $env:ProgramFiles 'Antigravity'),
    (Join-Path $env:ProgramFiles 'Antigravity IDE'),
    (Join-Path $env:LOCALAPPDATA 'agy\bin\agy.exe')
) | Where-Object { $_ }

Add-Report '[Installation candidates]'
foreach ($path in $candidateInstallations | Select-Object -Unique) { Add-PathRecord 'Candidate' $path }
foreach ($registryPath in @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)) {
    Get-ItemProperty $registryPath | Where-Object { $_.DisplayName -match '(?i)antigravity|agy' } | ForEach-Object {
        Add-Report ("Installed entry: {0} | version={1} | location={2}" -f $_.DisplayName, (Format-Value $_.DisplayVersion), (Format-Value $_.InstallLocation))
    }
}
Add-Report ''

$agy = Get-Command agy -ErrorAction SilentlyContinue
Add-Report '[CLI]'
if ($agy) {
    Add-Report ("agy path: {0}" -f $agy.Source)
    Add-Report ("agy version: {0}" -f (Invoke-Version $agy.Source))
} else {
    Add-Report 'agy: not found on PATH'
    $standardAgy = Join-Path $env:LOCALAPPDATA 'agy\bin\agy.exe'
    if (Test-Path -LiteralPath $standardAgy -PathType Leaf) {
        Add-Report ("agy standard path: {0}" -f $standardAgy)
        Add-Report ("agy version: {0}" -f (Invoke-Version $standardAgy))
    }
}
Add-Report ''

$processes = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -match '(?i)antigravity|agy|language[_-]?server|codeium'
})
$processById = @{}
foreach ($process in $processes) { $processById[[string]$process.ProcessId] = $process }
Add-Report '[Processes and relationships]'
if ($processes.Count -eq 0) {
    Add-Report 'No matching Antigravity process was found.'
} else {
    foreach ($process in $processes) {
        $parent = $processById[[string]$process.ParentProcessId]
        $parentText = if ($parent) { "$($parent.Name) (PID $($parent.ProcessId))" } else { "PID $($process.ParentProcessId)" }
        Add-Report ("name={0} | pid={1} | parent={2} | executable={3} | commandLine={4}" -f
            $process.Name, $process.ProcessId, $parentText, (Format-Value $process.ExecutablePath), (Redact-CommandLine $process.CommandLine))
    }
}
Add-Report ''

$candidateDirectories = @(
    (Join-Path $env:LOCALAPPDATA 'Antigravity'),
    (Join-Path $env:APPDATA 'Antigravity'),
    (Join-Path $env:LOCALAPPDATA 'Google\Antigravity'),
    (Join-Path $env:APPDATA 'Google\Antigravity'),
    (Join-Path $env:LOCALAPPDATA 'agy'),
    (Join-Path $env:APPDATA 'agy'),
    (Join-Path $env:USERPROFILE '.gemini'),
    (Join-Path $env:USERPROFILE '.antigravity')
) | Where-Object { $_ } | Select-Object -Unique

Add-Report '[Candidate application-data directories]'
foreach ($directory in $candidateDirectories) {
    if (Test-Path -LiteralPath $directory -PathType Container) {
        Add-Report ("directory: {0}" -f $directory)
    }
}
Add-Report ''

Add-Report '[Candidate session/history/log metadata]'
foreach ($directory in $candidateDirectories) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { continue }
    Add-Report ("root: {0}" -f $directory)
    $files = @(Get-ChildItem -LiteralPath $directory -File -Recurse -Depth 4 -Force | Where-Object {
        $_.Name -match '(?i)session|history|conversation|workspace|state|log|event|mcp' -or
        $_.Extension -match '(?i)^(\.db|\.sqlite|\.sqlite3|\.jsonl|\.log|\.wal|\.ldb)$'
    } | Select-Object -First 500)
    foreach ($file in $files) {
        Add-Report ("file: {0} | extension={1} | length={2} | modified={3}" -f
            $file.FullName, $file.Extension, $file.Length, $file.LastWriteTimeUtc.ToString('o'))
    }
}

$parent = Split-Path -Parent $OutputPath
if ($parent -and -not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
$report | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output ("Diagnostic report written to {0}" -f (Resolve-Path -LiteralPath $OutputPath))

param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$Test)
$ErrorActionPreference = 'Stop'
$nativeRoot = Join-Path $PSScriptRoot '../native/PhoneBox.Camera'
$buildPath = Join-Path $nativeRoot 'dist/build'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Install Visual Studio C++ Build Tools and Windows SDK 22000 or later to build PhoneBox Camera.' }
$version = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion
if (-not $version) { $version = if ($installation -match '2022') { '17' } else { '18' } }
$generator = if ($version.StartsWith('17')) { 'Visual Studio 17 2022' } else { 'Visual Studio 18 2026' }
& cmake -S $nativeRoot -B $buildPath -G $generator -A x64 "-DCMAKE_GENERATOR_INSTANCE=$installation"
if ($LASTEXITCODE) { throw 'Native camera configuration failed' }
& cmake --build $buildPath --config $Configuration --parallel
if ($LASTEXITCODE) { throw 'Native camera build failed' }
if ($Test) {
    & ctest --test-dir $buildPath -C $Configuration --output-on-failure --timeout 30
    if ($LASTEXITCODE) { throw 'Native camera tests failed' }
}

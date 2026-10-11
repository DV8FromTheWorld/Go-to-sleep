# Builds bin\GoToSleep.exe with the C# compiler that ships with Windows (.NET Framework 4.x). -Test also runs the logic tests.
# -DebugBuild also builds bin\GoToSleep.Debug.exe, which has the escape hotkey. install.ps1 never uses it.
param([switch]$Test, [switch]$DebugBuild)
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin = Join-Path $PSScriptRoot 'bin'
$src = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$fw = Split-Path $csc
$winmd = Join-Path $env:WINDIR 'System32\WinMetadata'
$refs = '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll',
        # Windows media controls (pause/resume Spotify, YouTube, ...)
        "/r:$winmd\Windows.Media.winmd", "/r:$winmd\Windows.Foundation.winmd", "/r:$fw\System.Runtime.dll"
New-Item -ItemType Directory -Force $bin | Out-Null

& $csc /nologo /target:winexe /optimize+ "/out:$bin\GoToSleep.exe" "/win32manifest:$PSScriptRoot\src\app.manifest" @refs @src
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $bin\GoToSleep.exe"

if ($DebugBuild) {
    & $csc /nologo /target:winexe /define:DEBUG "/out:$bin\GoToSleep.Debug.exe" "/win32manifest:$PSScriptRoot\src\app.manifest" @refs @src
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed' }
    Write-Host "Built $bin\GoToSleep.Debug.exe"
}

if ($Test) {
    $tests = Join-Path $PSScriptRoot 'tests\Tests.cs'
    & $csc /nologo /target:exe /main:GoToSleep.Tests "/out:$bin\Tests.exe" @refs @src $tests
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
    & "$bin\Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

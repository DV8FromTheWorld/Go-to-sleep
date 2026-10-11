# Safe test run: builds the debug exe and makes it lock soon, for a few minutes.
# Ways out: the "DEBUG: Exit" button on the lock screen, the lock ending at "wake time", or the 15 minute auto-exit.
# Uses bin\debug-data (not the real install's settings) and registers no scheduled task.
# Run it from an admin PowerShell to test everything; without admin, disabling Task Manager is skipped.
#
# -BorrowedLastNight 20 pretends you borrowed 20 min the night before, so tonight locks 20 min early with a smaller
# pool, the way a real second night would. It still locks -LockInMinutes from now.
param(
    [int]$LockInMinutes = 2,
    [int]$LockForMinutes = 5,
    [ValidateRange(0, 30)][int]$BorrowedLastNight = 0
)
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'build.ps1') -DebugBuild

$exe  = Join-Path $PSScriptRoot 'bin\GoToSleep.Debug.exe'
$data = Join-Path $PSScriptRoot 'bin\debug-data'
Get-Process GoToSleep.Debug -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $data | Out-Null
Remove-Item (Join-Path $data 'state.ini') -ErrorAction SilentlyContinue

# Round up to a whole minute so bedtime is never sooner than asked. With last night's borrowing, the normal bedtime
# sits that much later so the early lock still lands at $bed.
$now = Get-Date
$bed = $now.AddMinutes($LockInMinutes).AddSeconds(60 - $now.Second)
$normalBed = $bed.AddMinutes($BorrowedLastNight)
$wake = $normalBed.AddMinutes($LockForMinutes)
@"
Bedtime=$($normalBed.ToString('HH:mm'))
WakeTime=$($wake.ToString('HH:mm'))
WarnMinutes=2,1
GraceSeconds=60
BorrowMinutes=30
"@ | Set-Content -Path (Join-Path $data 'config.ini') -Encoding ASCII

if ($BorrowedLastNight -gt 0) {
    # A finished "last night" that borrowed some of its pool. On startup the app rolls over into tonight and
    # carries the debt, exactly as it does after a real night.
    $ic = [Globalization.CultureInfo]::InvariantCulture
    $tonight = ($now - $wake.TimeOfDay).Date
    @"
SessionDate=$($tonight.AddDays(-1).ToString('yyyy-MM-dd', $ic))
Bedtime=$($normalBed.TimeOfDay.ToString('hh\:mm\:ss', $ic))
WakeTime=$($wake.TimeOfDay.ToString('hh\:mm\:ss', $ic))
GraceSeconds=60
BorrowMinutes=30
DebtMinutes=0
BorrowedSeconds=$($BorrowedLastNight * 60)
GraceUsed=1
UnlockKind=
"@ | Set-Content -Path (Join-Path $data 'state.ini') -Encoding ASCII
}

Start-Process $exe
Write-Host ""
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "Not admin: Task Manager won't be disabled in this test run." -ForegroundColor Yellow }
Write-Host "Debug build running. Locks at $($bed.ToString('t')), unlocks at $($wake.ToString('t'))."
if ($BorrowedLastNight -gt 0) {
    Write-Host "  Simulating $BorrowedLastNight min borrowed last night: normal bedtime $($normalBed.ToString('t')), pool $(30 - $BorrowedLastNight) min."
}
Write-Host "  Escape:  'DEBUG: Exit' button on the lock screen (or it exits by itself 15 min after starting)"
Write-Host "  Before the lock: Stop-Process -Name GoToSleep.Debug"
Write-Host "  Log:     $data\log.txt"

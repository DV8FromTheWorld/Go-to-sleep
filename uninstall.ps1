#Requires -RunAsAdministrator
# Removes Go To Sleep. Deliberately refuses within 3 hours of bedtime, and asks you to type a sentence.
$ErrorActionPreference = 'Stop'

$taskName   = 'GoToSleep'
$installDir = Join-Path $env:ProgramFiles 'GoToSleep'
$dataDir    = Join-Path $env:ProgramData 'GoToSleep'
$exe        = Join-Path $installDir 'GoToSleep.exe'

if (Test-Path $exe) {
    $check = Start-Process $exe -ArgumentList '--can-uninstall' -Wait -PassThru
    if ($check.ExitCode -ne 0) {
        Write-Host "Not now. Uninstalling only works more than 3 hours before bedtime, and not while locked."
        Write-Host "If you still want this tomorrow afternoon, it'll work then."
        exit 1
    }
}

$phrase = 'I am removing Go To Sleep with a clear head, not to stay up later'
Write-Host "Type this exactly to continue:"
Write-Host "  $phrase"
if ((Read-Host '>') -cne $phrase) {
    Write-Host 'That did not match. Nothing was removed.'
    exit 1
}

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Get-Process GoToSleep -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' -Name DisableTaskMgr -ErrorAction SilentlyContinue
Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Go To Sleep removed.'

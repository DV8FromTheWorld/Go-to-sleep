#Requires -RunAsAdministrator
# Installs Go To Sleep: builds it, copies it to Program Files, and registers a scheduled task that keeps it running.
# Re-running is safe (use it to update). Pass -Bedtime / -WakeTime to change times; they apply from the next wake time.
param(
    [string]$Bedtime,
    [string]$WakeTime
)
$ErrorActionPreference = 'Stop'

$taskName   = 'GoToSleep'
$installDir = Join-Path $env:ProgramFiles 'GoToSleep'
$dataDir    = Join-Path $env:ProgramData 'GoToSleep'
$exe        = Join-Path $installDir 'GoToSleep.exe'
$configPath = Join-Path $dataDir 'config.ini'
$user       = "$env:USERDOMAIN\$env:USERNAME"

function Read-Time([string]$prompt, [string]$default) {
    while ($true) {
        $value = Read-Host "$prompt [$default]"
        if (-not $value) { $value = $default }
        try { return ([datetime]::Parse($value)).ToString('HH:mm') } catch { Write-Host "  Couldn't read '$value' as a time. Try 01:00 or 11:30 PM." }
    }
}

& (Join-Path $PSScriptRoot 'build.ps1')

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Get-Process GoToSleep -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'bin\GoToSleep.exe') $exe -Force

New-Item -ItemType Directory -Force $dataDir | Out-Null
if (-not (Test-Path $configPath) -or $Bedtime -or $WakeTime) {
    if ($Bedtime) { $Bedtime = ([datetime]::Parse($Bedtime)).ToString('HH:mm') } else { $Bedtime = Read-Time 'Bedtime (PC locks at)' '01:00' }
    if ($WakeTime) { $WakeTime = ([datetime]::Parse($WakeTime)).ToString('HH:mm') } else { $WakeTime = Read-Time 'Wake time (PC unlocks at)' '07:00' }
    @"
; Go To Sleep settings. Edit from an admin terminal: notepad $configPath
; Changes take effect from the next wake time, never tonight.
Bedtime=$Bedtime
WakeTime=$WakeTime
; Minutes before bedtime to show a warning popup
WarnMinutes=30,15,5,1
; One-time "wrap up" unlock per night
GraceSeconds=60
; Max minutes you can borrow from tomorrow (once per night)
BorrowMinutes=30
"@ | Set-Content -Path $configPath -Encoding ASCII
}

# Only admins (elevated) can change settings or state; normal processes can read.
icacls $dataDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
icacls "$dataDir\*" /reset /T /Q | Out-Null

# Starts at logon, elevated, and re-launches within a minute if the process is killed.
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Locks the PC at bedtime. Installed from $PSScriptRoot</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$user</UserId>
      <Repetition>
        <Interval>PT1M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"$exe"</Command>
    </Exec>
  </Actions>
</Task>
"@
Register-ScheduledTask -TaskName $taskName -Xml $xml | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host ""
Write-Host "Installed. Go To Sleep is running."
Write-Host "  Settings:  $configPath  (changes apply from the next wake time)"
Write-Host "  Log:       $dataDir\log.txt"
Write-Host "  Status:    & '$exe' --status"
Write-Host "  Preview:   & '$exe' --preview   (Esc to close)"

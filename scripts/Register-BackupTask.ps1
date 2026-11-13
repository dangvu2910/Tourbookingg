<#
.SYNOPSIS
  Schedule scripts\Backup.ps1 daily with Windows Task Scheduler (current user, no admin rights needed).
  Runs only while you are logged on (Docker Desktop needs your session). A run missed while the PC was
  off/asleep starts as soon as possible afterwards. Logs: backups\logs\backup-<timestamp>.log

.EXAMPLE
  .\scripts\Register-BackupTask.ps1                    # daily 02:00, keep 14 days
  .\scripts\Register-BackupTask.ps1 -At 21:30 -KeepDays 7
  .\scripts\Register-BackupTask.ps1 -RunNow            # register and run once now
  .\scripts\Register-BackupTask.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [string]$At = '02:00',
    [int]$KeepDays = 14,
    [string]$TaskName = 'Tourbooking Hybrid Backup',
    [switch]$RunNow,
    [switch]$Unregister
)
$ErrorActionPreference = 'Stop'
if ($Unregister) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed scheduled task '$TaskName'."
    return
}
$root = Split-Path $PSScriptRoot -Parent
$script = Join-Path $PSScriptRoot 'Backup.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -WorkingDirectory $root `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`" -KeepDays $KeepDays"
$trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact($At, 'HH:mm', $null))
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1) `
    -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description "Daily backup of the Tourbooking hybrid stack (SQL Server, MongoDB, key ring, images) to $root\backups" -Force | Out-Null
$info = Get-ScheduledTaskInfo -TaskName $TaskName
Write-Host "Scheduled '$TaskName' daily at $At (keep $KeepDays days). Next run: $($info.NextRunTime)"
if ($RunNow) { Start-ScheduledTask -TaskName $TaskName; Write-Host 'Started a run now; see backups\logs.' }

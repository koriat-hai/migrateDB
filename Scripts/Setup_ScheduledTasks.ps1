# הרץ כ-Administrator
# Right-click → "Run with PowerShell" (or: powershell -ExecutionPolicy Bypass -File Setup_ScheduledTasks.ps1)

$workerPath = "C:\inetpub\wwwroot\migrateDB\Worker"

# ── Task 1: סנכרון היום — כל 5 דקות ──────────────────────────────────────────
$action1  = New-ScheduledTaskAction `
    -Execute "$workerPath\RunWorker_Today.bat" `
    -WorkingDirectory $workerPath

$trigger1 = New-ScheduledTaskTrigger -Daily -At "01:00AM"
$trigger1.Repetition = (New-ScheduledTaskTrigger `
    -Once -At "01:00AM" `
    -RepetitionInterval (New-TimeSpan -Minutes 5) `
    -RepetitionDuration (New-TimeSpan -Days 1)).Repetition

$settings1 = New-ScheduledTaskSettingsSet `
    -MultipleInstances    IgnoreNew `
    -ExecutionTimeLimit   (New-TimeSpan -Minutes 10) `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

Register-ScheduledTask `
    -TaskName   "SmartScaleWorkerTask_Today" `
    -Action     $action1 `
    -Trigger    $trigger1 `
    -Settings   $settings1 `
    -RunLevel   Highest `
    -Description "SmartScale Worker - סנכרון היום (כל 5 דקות)" `
    -Force

Write-Host "✔ SmartScaleWorkerTask_Today נרשמה" -ForegroundColor Green

# ── Task 2: סריקה מלאה 90 יום — פעם ביום 18:00 ───────────────────────────────
$action2  = New-ScheduledTaskAction `
    -Execute "$workerPath\RunWorker_90Days.bat" `
    -WorkingDirectory $workerPath

$trigger2 = New-ScheduledTaskTrigger -Daily -At "06:00PM"

$settings2 = New-ScheduledTaskSettingsSet `
    -MultipleInstances    IgnoreNew `
    -ExecutionTimeLimit   (New-TimeSpan -Minutes 15) `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

Register-ScheduledTask `
    -TaskName   "SmartScaleWorkerTask_90Days" `
    -Action     $action2 `
    -Trigger    $trigger2 `
    -Settings   $settings2 `
    -RunLevel   Highest `
    -Description "SmartScale Worker - סריקה מלאה 90 יום (18:00)" `
    -Force

Write-Host "✔ SmartScaleWorkerTask_90Days נרשמה" -ForegroundColor Green
Write-Host ""
Write-Host "סיום. Tasks פעילות:" -ForegroundColor Cyan
Get-ScheduledTask | Where-Object { $_.TaskName -like "*SmartScale*" } | Select-Object TaskName, State

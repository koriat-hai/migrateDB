# הרץ כ-Administrator
# Right-click → "Run with PowerShell" (or: powershell -ExecutionPolicy Bypass -File Setup_ScheduledTasks.ps1)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── Task 1: סנכרון היום — כל 5 דקות ──────────────────────────────────────────
schtasks /create /tn "Task_Today" /xml "$scriptDir\Task_Today.xml" /f
if ($LASTEXITCODE -eq 0) {
    Write-Host "Task_Today נרשמה בהצלחה" -ForegroundColor Green
} else {
    Write-Host "שגיאה ברישום Task_Today (קוד: $LASTEXITCODE)" -ForegroundColor Red
}

# ── Task 2: סריקה מלאה 90 יום — פעם ביום 18:00 ───────────────────────────────
schtasks /create /tn "Task_90Days" /xml "$scriptDir\Task_90Days.xml" /f
if ($LASTEXITCODE -eq 0) {
    Write-Host "Task_90Days נרשמה בהצלחה" -ForegroundColor Green
} else {
    Write-Host "שגיאה ברישום Task_90Days (קוד: $LASTEXITCODE)" -ForegroundColor Red
}

Write-Host ""
Write-Host "Tasks פעילות:" -ForegroundColor Cyan
schtasks /query /tn "Task_Today"  /fo LIST /v 2>$null | Select-String "Status|Last Run|Next Run|Last Result|Logon Mode"
schtasks /query /tn "Task_90Days" /fo LIST /v 2>$null | Select-String "Status|Last Run|Next Run|Last Result|Logon Mode"

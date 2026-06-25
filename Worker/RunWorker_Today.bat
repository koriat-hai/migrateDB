@echo off
net use H: "\\serverdb\servercust" 2>nul
cd /d "C:\inetpub\wwwroot\migrateDB\Worker"
"C:\inetpub\wwwroot\migrateDB\Worker\SmartScale.Worker.exe" --window-days 0 --table reports

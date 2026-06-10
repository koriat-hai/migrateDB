# SmartScale Sync — Access → SQL Server

שירות סנכרון אוטומטי מקבצי Microsoft Access לבסיס נתונים SQL Server,
עם ממשק ניהול ASP.NET Core מבוסס-IIS.

---

## ארכיטקטורה

```
SmartScale.sln
├── src/SmartScale.Shared/    — מודלים + גישה ל-migrateDB (Dapper)
├── src/SmartScale.Worker/    — תהליך Console, מופעל ע"י Task Scheduler כל 5 דק'
└── src/SmartScale.Web/       — ממשק ניהול ASP.NET Core MVC על IIS
```

כל הרכיבים חולקים DB ניהולי בשם **`migrateDB`**.

---

## דרישות קדם

| רכיב | גרסה מינימלית | הערה |
|------|--------------|------|
| Windows Server | 2019+ | |
| .NET 8 SDK | 8.x | לבנייה בלבד; Runtime בשרת IIS |
| .NET 8 Hosting Bundle | 8.x | לפריסה על IIS |
| IIS | 10+ | עם ASP.NET Core Module V2 |
| SQL Server | 2017+ | Windows Authentication |
| **Microsoft Access Database Engine 2016 Redistributable (x64)** | 16.x | **חובה ל-Worker**. הורד מ-Microsoft ולא מ-Office. לא תואם ל-Office 32-bit באותו מחשב — ראה הערה למטה. |

### הערה על ACE OLEDB
- הורד את **AccessDatabaseEngine_X64.exe** (הגרסה x64) מ:  
  `https://www.microsoft.com/en-us/download/details.aspx?id=54920`
- ה-Worker בנוי כ-**x64** — חייב את גרסת ה-x64 של ה-driver.
- אם מותקן Office 32-bit, ייתכן שתצטרך `/quiet` flag בהתקנה כדי לעקוף בדיקת ה-bitness.

---

## שלב 1 — יצירת migrateDB

ב-SQL Server Management Studio, על המחשב שמריץ את ה-Worker:

```sql
CREATE DATABASE migrateDB;
GO
```

אחר כך הרץ את הסקריפט:

```
Scripts\CreateMigrateDB.sql
```

---

## שלב 2 — הגדרת Connection Strings

ערוך שני קבצים:

**`src/SmartScale.Worker/appsettings.json`**
```json
{
  "ConnectionStrings": {
    "MigrateDb": "Server=MY_SERVER;Database=migrateDB;Integrated Security=True;TrustServerCertificate=True"
  },
  "AppSettings": {
    "SqlServerName": "MY_SERVER"
  }
}
```

**`src/SmartScale.Web/appsettings.json`**
```json
{
  "ConnectionStrings": {
    "MigrateDb": "Server=MY_SERVER;Database=migrateDB;Integrated Security=True;TrustServerCertificate=True"
  },
  "AppSettings": {
    "SqlServerName": "MY_SERVER",
    "WorkerExePath": "C:\\SmartScale\\Worker\\SmartScale.Worker.exe"
  }
}
```

> `WorkerExePath` — נתיב מלא לקובץ ה-exe של ה-Worker לאחר פרסום. משמש לכפתור "הרץ עכשיו".  
> אם הנתיב לא מוגדר, הכפתור עדיין פועל — הוא מסמן בקשה ב-DB וה-Worker מבצע אותה בריצה הבאה.

---

## שלב 3 — בנייה

דרוש .NET 8 SDK (על מחשב הפיתוח או ה-build agent):

```powershell
cd C:\inetpub\wwwroot\migrateDB
dotnet publish src\SmartScale.Worker\SmartScale.Worker.csproj `
    -c Release -r win-x64 --self-contained false `
    -o C:\SmartScale\Worker

dotnet publish src\SmartScale.Web\SmartScale.Web.csproj `
    -c Release -o C:\SmartScale\Web
```

---

## שלב 4 — פרסום GUI על IIS

1. פתח IIS Manager
2. צור **Application Pool** חדש:
   - שם: `SmartScalePool`
   - .NET CLR Version: **No Managed Code**
   - Identity: חשבון שיש לו גישה ל-SQL Server ב-Windows Authentication
3. צור **Website** או **Application** חדש:
   - Physical path: `C:\SmartScale\Web`
   - Application Pool: `SmartScalePool`
   - Binding: `http://localhost:5100` (או כל port שתרצה)
4. ודא שה-`web.config` בתיקיית הפרסום תקין (נוצר אוטומטית בבנייה).

---

## שלב 5 — הגדרת Windows Task Scheduler

הרץ ב-PowerShell כ-Administrator כדי לרשום task שמריץ את ה-Worker כל 5 דקות:

```powershell
$action  = New-ScheduledTaskAction `
    -Execute "C:\SmartScale\Worker\SmartScale.Worker.exe"

$trigger = New-ScheduledTaskTrigger `
    -RepetitionInterval (New-TimeSpan -Minutes 5) `
    -Once -At (Get-Date)

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 4) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

$principal = New-ScheduledTaskPrincipal `
    -UserId "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName "SmartScaleSync" `
    -TaskPath "\SmartScale\" `
    -Action   $action `
    -Trigger  $trigger `
    -Settings $settings `
    -Principal $principal `
    -Force
```

> **חשוב:** שנה את `-UserId "SYSTEM"` לחשבון שיש לו:
> 1. גישה ל-SQL Server ב-Windows Authentication
> 2. גישת קריאה לתיקיות ה-.accdb ב-Synology Drive

לבדיקה ידנית:
```powershell
Start-ScheduledTask -TaskName "\SmartScale\SmartScaleSync"
```

---

## תזרים הסנכרון

```
Task Scheduler (כל 5 דק')
    └─► SmartScale.Worker.exe
            ├── לכל לקוח פעיל:
            │     1. מצא .accdb עדכני ב-AccessFolderPath
            │     2. Debounce — המתן 5 שניות, בדוק שהקובץ לא משתנה (Synology sync)
            │     3. התחבר ב-OLEDB (קריאה-בלבד)
            │     4. חלץ סכמה אוטומטית (Tables / Columns / Primary_Keys)
            │     5. צור טבלאות חסרות ב-TargetDatabase
            │     6. לכל טבלה:
            │           • קרא כל שורות
            │           • חשב MD5 hash לכל שורה
            │           • השווה ל-SyncState
            │           • INSERT / UPDATE בלבד (אין DELETE)
            │     7. כתוב לוג ל-SyncLog
            └── יצא
```

---

## כפתור "הרץ עכשיו"

- מסמן `RunRequestedAt = NOW()` ב-DB
- מנסה להפעיל את `WorkerExePath` ישירות (`Process.Start`)
- אם ההפעלה נכשלת (הרשאות IIS) — הסנכרון יבוצע בריצה הקרובה של Task Scheduler

---

## מבנה migrateDB

| טבלה | תיאור |
|------|-------|
| `Clients` | רשימת לקוחות + נתיב Access + DB יעד |
| `SyncLog` | לוג ריצה לכל טבלה (insert/update/error) |
| `SyncState` | hash לכל שורה — לזיהוי שינויים בין ריצות |

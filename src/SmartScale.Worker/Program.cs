using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartScale.Worker;
using SmartScale.Worker.Services;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; });
    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger("Main");

// מניעת ריצות מקבילות — רק instance אחד בכל רגע
// --manual: ריצה ידנית מהאתר — ממתין עד 30 דק' לmutex (Task_Today ייסיים תוך דקות)
// ללא --manual: ריצה מתוזמנת — ממתין 30 שניות בלבד
bool isManualRun = Array.IndexOf(args, "--manual") >= 0;
var mutexTimeout = isManualRun ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30);

using var mutex = new Mutex(false, "SmartScaleWorker");
bool acquiredMutex = mutex.WaitOne(mutexTimeout);
if (!acquiredMutex)
{
    if (isManualRun)
        logger.LogWarning("Worker לא הצליח לרכוש mutex אחרי 30 דק' — יוצא ({Time})", DateTime.Now);
    else
        logger.LogInformation("Worker כבר רץ — מדלג על ריצה מתוזמנת ({Time})", DateTime.Now);
    return;
}

// --window-days N  → מחליף את WindowDays מה-config לכל הטבלאות
// --window-days 0  → רק שורות של היום (מחצות)
// --table NAME     → סנכרן טבלה אחת בלבד (case-insensitive)
// ללא ארגומנט     → משתמש ב-config (ברירת מחדל 90 יום)
int? windowDaysOverride = null;
var wdArg = Array.FindIndex(args, a => a == "--window-days");
if (wdArg >= 0 && wdArg + 1 < args.Length && int.TryParse(args[wdArg + 1], out var wdVal))
    windowDaysOverride = wdVal;

string? tableFilter = null;
var tArg = Array.FindIndex(args, a => a == "--table");
if (tArg >= 0 && tArg + 1 < args.Length)
    tableFilter = args[tArg + 1];

logger.LogInformation("SmartScale Worker מתחיל — {Time} | חלון: {Mode} | טבלה: {Table}",
    DateTime.Now,
    windowDaysOverride.HasValue ? (windowDaysOverride == 0 ? "היום בלבד" : $"{windowDaysOverride} ימים") : "ברירת מחדל (config)",
    tableFilter ?? "הכל");

try
{
    var migrateConnStr = config.GetConnectionString("MigrateDb")
        ?? throw new InvalidOperationException("חסר ConnectionStrings:MigrateDb ב-appsettings.json");

    var sqlServerName = config["AppSettings:SqlServerName"]
        ?? throw new InvalidOperationException("חסר AppSettings:SqlServerName ב-appsettings.json");
    var sqlUsername         = config["AppSettings:SqlUsername"]         ?? "";
    var sqlPassword         = config["AppSettings:SqlPassword"]         ?? "";
    var accessDbPassword    = config["AppSettings:AccessDbPassword"]    ?? "";
    var maxConcurrent       = int.TryParse(config["AppSettings:MaxConcurrentClients"], out var mc) ? mc : 10;

    var windowedTables = config.GetSection("WindowSync:Tables")
        .Get<List<WindowedTable>>() ?? new List<WindowedTable>();

    // דריסת WindowDays מהארגומנט אם סופק
    if (windowDaysOverride.HasValue)
        windowedTables = windowedTables
            .Select(w => w with { WindowDays = windowDaysOverride.Value })
            .ToList();

    var repo         = new MigrateDbRepository(migrateConnStr);
    var orchestrator = new SyncOrchestrator(repo, sqlServerName, sqlUsername, sqlPassword, accessDbPassword, loggerFactory, windowedTables, tableFilter, maxConcurrent);

    await orchestrator.RunAsync();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "שגיאה קריטית ב-Worker");
    Environment.Exit(1);
}

logger.LogInformation("SmartScale Worker סיים — {Time}", DateTime.Now);

using System.Data.OleDb;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SmartScale.Worker;

namespace SmartScale.Worker.Services;

public record WindowedTable(string TableName, string DateColumn, int WindowDays);

public class SyncOrchestrator
{
    private readonly MigrateDbRepository                    _repo;
    private readonly string                                 _sqlServerName;
    private readonly string                                 _sqlUsername;
    private readonly string                                 _sqlPassword;
    private readonly string                                 _accessDbPassword;
    private readonly ILoggerFactory                         _loggerFactory;
    private readonly ILogger                                _logger;
    private readonly IReadOnlyDictionary<string, WindowedTable> _windowedTables;
    private readonly string? _tableFilter;
    private readonly int     _maxConcurrentClients;

    public SyncOrchestrator(MigrateDbRepository repo, string sqlServerName,
        string sqlUsername, string sqlPassword, string accessDbPassword,
        ILoggerFactory loggerFactory,
        IEnumerable<WindowedTable>? windowedTables = null,
        string? tableFilter = null,
        int maxConcurrentClients = 10)
    {
        _repo                  = repo;
        _sqlServerName         = sqlServerName;
        _sqlUsername           = sqlUsername;
        _sqlPassword           = sqlPassword;
        _accessDbPassword      = accessDbPassword;
        _loggerFactory         = loggerFactory;
        _logger                = loggerFactory.CreateLogger<SyncOrchestrator>();
        _windowedTables        = (windowedTables ?? Enumerable.Empty<WindowedTable>())
            .ToDictionary(w => w.TableName, StringComparer.OrdinalIgnoreCase);
        _tableFilter           = tableFilter;
        _maxConcurrentClients  = maxConcurrentClients;
    }

    public async Task RunAsync()
    {
        var clients = (await _repo.GetActiveClientsAsync()).ToList();
        var maxConcurrent = await _repo.GetGlobalSettingIntAsync("MaxConcurrentClients", _maxConcurrentClients);
        _logger.LogInformation("נמצאו {Count} לקוחות פעילים | מקביליות: {Max}", clients.Count, maxConcurrent);

        var sem = new SemaphoreSlim(maxConcurrent);
        var tasks = clients.Select(async client =>
        {
            await sem.WaitAsync();
            try
            {
                _logger.LogInformation("─── מעבד לקוח: {Name} (Id={Id}) ───", client.ClientName, client.ClientId);
                await ProcessClientAsync(client);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "שגיאה לא-מטופלת ללקוח {Id}", client.ClientId);
            }
            finally
            {
                if (client.RunRequestedAt.HasValue)
                    await _repo.ClearRunRequestedAsync(client.ClientId);
                sem.Release();
            }
        });
        await Task.WhenAll(tasks);

        // מחק לוגים ישנים מ-48 שעות
        await _repo.DeleteOldLogsAsync();
    }

    private async Task ProcessClientAsync(Client client)
    {
        // שלב 1: איתור קובץ .accdb
        if (!Directory.Exists(client.AccessFolderPath))
        {
            _logger.LogWarning("תיקייה לא קיימת: {Path}", client.AccessFolderPath);
            await WriteSkipLog(client, "*", $"תיקייה לא נמצאת: {client.AccessFolderPath}");
            return;
        }

        var accPath = Path.Combine(client.AccessFolderPath, "ScaleDB.accdb");
        if (!File.Exists(accPath))
        {
            _logger.LogWarning("קובץ ScaleDB.accdb לא נמצא ב-{Path}", client.AccessFolderPath);
            await WriteSkipLog(client, "*", "לא נמצא ScaleDB.accdb בתיקייה");
            return;
        }

        var conflictFiles = Directory.GetFiles(client.AccessFolderPath, "ScaleDB_*_Conflict.accdb");
        if (conflictFiles.Length > 0)
            _logger.LogWarning("נמצאו {Count} קבצי Conflict בתיקייה — מתעלם מהם", conflictFiles.Length);

        _logger.LogInformation("קובץ Access: {Path}", accPath);

        // שלב 2: Debounce — וידוא שהקובץ לא באמצע סנכרון Synology
        var swTotal = Stopwatch.StartNew();
        var lwt1 = File.GetLastWriteTime(accPath);
        await Task.Delay(TimeSpan.FromSeconds(5));
        var lwt2 = File.GetLastWriteTime(accPath);

        if (lwt1 != lwt2)
        {
            _logger.LogInformation("הקובץ {Path} עדיין מסתנכרן — מדלג", accPath);
            await WriteSkipLog(client, "*", "הקובץ בתהליך סנכרון Synology Drive — יטופל בריצה הבאה");
            return;
        }

        // שלב 2ב: בדיקת mtime — אם הקובץ לא השתנה מאז הסנכרון האחרון, אין מה לסנכרן
        // בשבת — דולגים על הבדיקה כדי לאפשר סריקה מלאה של כל הנתונים (כולל שינויים היסטוריים)
        bool isSaturday  = DateTime.Now.DayOfWeek == DayOfWeek.Saturday;
        var mtimeKey     = _tableFilter != null ? $"file_mtime_{_tableFilter}" : "file_mtime";
        var currentMtime = lwt2.Ticks.ToString();
        var storedMtime  = await _repo.GetMetaValueAsync(client.ClientId, mtimeKey);
        bool isFirstSync = storedMtime == null;   // לקוח חדש — אין היסטוריית סנכרון
        if (!isSaturday && !isFirstSync && storedMtime == currentMtime && !client.RunRequestedAt.HasValue)
        {
            _logger.LogInformation("קובץ לא השתנה מאז הסנכרון האחרון — מדלג ({Mtime})", lwt2);
            return;
        }
        if (isFirstSync)
            _logger.LogInformation("סנכרון ראשון ללקוח {Name} — סריקה מלאה של כל ההיסטוריה", client.ClientName);
        else if (isSaturday && storedMtime == currentMtime)
            _logger.LogInformation("שבת — מתעלם מבדיקת mtime לסריקה היסטורית מלאה");
        else if (client.RunRequestedAt.HasValue && storedMtime == currentMtime)
            _logger.LogInformation("סנכרון מאולץ — מתעלם מבדיקת mtime");

        // שלב 3א: העתק את קובץ Access לתיקיה מקומית — קריאה מקומית מהירה מאשר דרך הרשת
        var localAccPath = Path.Combine(Path.GetTempPath(), $"SmartScale_{client.ClientId}.accdb");
        var swCopy = Stopwatch.StartNew();
        try
        {
            File.Copy(accPath, localAccPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "שגיאה בהעתקת קובץ Access ל-Temp עבור {Name}", client.ClientName);
            await WriteSkipLog(client, "*", $"שגיאה בהעתקת קובץ Access: {ex.Message}");
            return;
        }
        swCopy.Stop();
        _logger.LogInformation("⏱ העתקת Access לוקאלית: {E:N1}s ({Path})", swCopy.Elapsed.TotalSeconds, localAccPath);

        try
        {

        var accessConnStr = ResolveAccessConnStr(localAccPath, client.ClientName);
        if (accessConnStr == null)
        {
            await WriteSkipLog(client, "*", "שגיאה בחיבור ל-Access: סיסמה לא חוקית.");
            return;
        }
        var targetConnStr = string.IsNullOrEmpty(_sqlUsername)
            ? $"Server={_sqlServerName};Database={client.TargetDatabase};Integrated Security=True;TrustServerCertificate=True;"
            : $"Server={_sqlServerName};Database={client.TargetDatabase};User Id={_sqlUsername};Password={_sqlPassword};TrustServerCertificate=True;";

        // שלב 3ב: חילוץ סכמה
        var swSchema = Stopwatch.StartNew();
        var extractor = new AccessSchemaExtractor(_loggerFactory.CreateLogger<AccessSchemaExtractor>());
        List<TableSchema> schemas;
        try
        {
            schemas = extractor.ExtractSchemas(accessConnStr, _tableFilter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "שגיאה בקריאת סכמה מ-Access עבור {Name}", client.ClientName);
            await WriteSkipLog(client, "*", $"שגיאה בחיבור ל-Access: {ex.Message}");
            return;
        }
        swSchema.Stop();
        _logger.LogInformation("⏱ חילוץ סכמה: {Elapsed:N1}s, נמצאו {Count} טבלאות", swSchema.Elapsed.TotalSeconds, schemas.Count);

        var tableManager = new TargetTableManager(targetConnStr, _loggerFactory.CreateLogger<TargetTableManager>());
        var dataSyncer   = new DataSyncer(_repo, accessConnStr, targetConnStr, _loggerFactory.CreateLogger<DataSyncer>());

        bool fullScan = isSaturday || isFirstSync;
        if (isSaturday)
            _logger.LogInformation("שבת — סריקה מלאה (ללא חלון תאריך, כולל נתונים היסטוריים)");

        foreach (var schema in schemas)
        {
            if (_tableFilter != null && !schema.TableName.Equals(_tableFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var startTime = DateTime.UtcNow;
            int inserted = 0, updated = 0;
            var status  = "Success";
            string? msg = null;

            string? windowCol   = null;
            DateTime? windowStart = null;
            if (!fullScan && _windowedTables.TryGetValue(schema.TableName, out var wt))
            {
                windowCol   = wt.DateColumn;
                // WindowDays=0 → מחצות של היום; אחרת → N ימים אחורה
                windowStart = wt.WindowDays == 0 ? DateTime.Today : DateTime.Now.AddDays(-wt.WindowDays);
                _logger.LogInformation("  [{Table}] חלון תאריך: {Col} >= {Date:yyyy-MM-dd}",
                    schema.TableName, windowCol, windowStart);
            }

            try
            {
                var swEnsure = Stopwatch.StartNew();
                tableManager.EnsureTableExists(schema);
                swEnsure.Stop();
                _logger.LogInformation("  [{Table}] ⏱ EnsureTable: {E:N1}s", schema.TableName, swEnsure.Elapsed.TotalSeconds);

                var swSync = Stopwatch.StartNew();
                (inserted, updated) = await dataSyncer.SyncTableAsync(client.ClientId, schema, windowCol, windowStart);
                swSync.Stop();
                _logger.LogInformation("  [{Table}] +{Ins} הכנסות, ~{Upd} עדכונים | ⏱ SyncTable: {S:N1}s", schema.TableName, inserted, updated, swSync.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                status = "Error";
                msg    = ex.Message;
                _logger.LogError(ex, "  [{Table}] שגיאה בסנכרון", schema.TableName);
            }

            await _repo.AddSyncLogAsync(new SyncLog
            {
                ClientId      = client.ClientId,
                RunStartedAt  = startTime,
                RunFinishedAt = DateTime.UtcNow,
                TableName     = schema.TableName,
                InsertCount   = inserted,
                UpdateCount   = updated,
                Status        = status,
                Message       = msg
            });
        }

        // שמור את ה-mtime הנוכחי — הסנכרון הבא ידלג אם הקובץ לא השתנה
        await _repo.SetMetaValueAsync(client.ClientId, mtimeKey, currentMtime);
        swTotal.Stop();
        _logger.LogInformation("⏱ סה״כ לקוח {Name}: {Total:N1}s (כולל 5s debounce)", client.ClientName, swTotal.Elapsed.TotalSeconds);

        } // try
        finally
        {
            try { File.Delete(localAccPath); } catch { /* לא קריטי */ }
        }
    }

    private string? ResolveAccessConnStr(string accPath, string clientName)
    {
        var noPassword  = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={accPath};Mode=Share Deny None;";
        var withPassword = string.IsNullOrEmpty(_accessDbPassword)
            ? null
            : $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={accPath};Mode=Share Deny None;Jet OLEDB:Database Password={_accessDbPassword};";

        // נסה עם סיסמה ראשון
        if (withPassword != null)
        {
            try
            {
                using var test = new OleDbConnection(withPassword);
                test.Open();
                return withPassword;
            }
            catch
            {
                _logger.LogWarning("חיבור עם סיסמה נכשל עבור {Name} — מנסה ללא סיסמה", clientName);
            }
        }

        // נסה ללא סיסמה
        try
        {
            using var test = new OleDbConnection(noPassword);
            test.Open();
            if (withPassword != null)
                _logger.LogWarning("קובץ Access של {Name} פתוח ללא סיסמה — בדוק הגדרות", clientName);
            return noPassword;
        }
        catch
        {
            return null;
        }
    }

    private Task WriteSkipLog(Client client, string table, string message) =>
        _repo.AddSyncLogAsync(new SyncLog
        {
            ClientId      = client.ClientId,
            RunStartedAt  = DateTime.UtcNow,
            RunFinishedAt = DateTime.UtcNow,
            TableName     = table,
            Status        = "Skipped",
            Message       = message
        });
}

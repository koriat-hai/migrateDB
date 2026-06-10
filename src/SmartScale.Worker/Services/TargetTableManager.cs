using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SmartScale.Worker.Services;

/// <summary>
/// אחראי ליצירת טבלאות ב-DB היעד אם הן עדיין לא קיימות.
/// אינו משנה טבלאות קיימות.
/// </summary>
public class TargetTableManager
{
    private readonly string                       _targetConnStr;
    private readonly ILogger<TargetTableManager>  _logger;

    public TargetTableManager(string targetConnStr, ILogger<TargetTableManager> logger)
    {
        _targetConnStr = targetConnStr;
        _logger        = logger;
    }

    public void EnsureTableExists(TableSchema schema)
    {
        using var conn = new SqlConnection(_targetConnStr);
        conn.Open();

        // בדוק אם הטבלה כבר קיימת
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @Name";
        checkCmd.Parameters.AddWithValue("@Name", schema.TableName);
        if ((int)checkCmd.ExecuteScalar()! > 0)
        {
            _logger.LogDebug("טבלה [{Table}] כבר קיימת ביעד", schema.TableName);
            return;
        }

        // בנה CREATE TABLE
        var pkSet = new HashSet<string>(schema.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        var colDefs = schema.Columns
            .Select(c => $"    [{c.Name}] {c.SqlType} {(pkSet.Contains(c.Name) ? "NOT NULL" : "NULL")}")
            .ToList();

        if (schema.PrimaryKeyColumns.Count > 0)
        {
            colDefs.Add("    [RowHash] CHAR(32) NULL");
            var pkCols = string.Join(", ", schema.PrimaryKeyColumns.Select(pk => $"[{pk}]"));
            colDefs.Add($"    CONSTRAINT [PK_{schema.TableName}] PRIMARY KEY ({pkCols})");
        }

        var ddl = $"CREATE TABLE [{schema.TableName}] (\n{string.Join(",\n", colDefs)}\n);";

        _logger.LogInformation("יוצר טבלה [{Table}] ביעד", schema.TableName);
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = ddl;
        createCmd.ExecuteNonQuery();
    }
}

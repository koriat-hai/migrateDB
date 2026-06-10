using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SmartScale.Worker;

namespace SmartScale.Worker.Services;

public class DataSyncer
{
    private readonly MigrateDbRepository _repo;
    private readonly string              _accessConnStr;
    private readonly string              _targetConnStr;
    private readonly ILogger<DataSyncer> _logger;

    public DataSyncer(MigrateDbRepository repo, string accessConnStr, string targetConnStr,
        ILogger<DataSyncer> logger)
    {
        _repo          = repo;
        _accessConnStr = accessConnStr;
        _targetConnStr = targetConnStr;
        _logger        = logger;
    }

    public async Task<(int inserted, int updated)> SyncTableAsync(int clientId, TableSchema schema,
        string? windowDateColumn = null, DateTime? windowStart = null)
    {
        if (schema.PrimaryKeyColumns.Count == 0)
            return await FullReplaceTableAsync(schema);

        var colNames  = schema.Columns.Select(c => c.Name).ToList();
        var insertSql = BuildInsertSql(schema.TableName, colNames);
        var updateSql = BuildUpdateSql(schema.TableName, colNames, schema.PrimaryKeyColumns);

        int inserted = 0, updated = 0;

        // בנה SELECT — date-window לסנכרון מלא של החלון (כולל עדכונים לשורות קיימות)
        var selectSql = windowDateColumn != null && windowStart.HasValue
            ? $"SELECT * FROM [{schema.TableName}] WHERE [{windowDateColumn}] >= #{windowStart.Value:yyyy-MM-dd}#"
            : $"SELECT * FROM [{schema.TableName}]";

        // קרא מ-Access לזיכרון וסגור חיבור מיד — לא להחזיק נעילה בזמן עבודת SQL Server
        var rows = new List<(string rowKey, string rowHash, object?[] values)>();
        var swAccess = Stopwatch.StartNew();
        using (var accessConn = new OleDbConnection(_accessConnStr))
        {
            var swOpen = Stopwatch.StartNew();
            accessConn.Open();
            swOpen.Stop();
            _logger.LogInformation("  ⏱ Access Open: {E:N1}s | SQL: {Sql}", swOpen.Elapsed.TotalSeconds, selectSql);

            var swRead = Stopwatch.StartNew();
            using (var selectCmd = new OleDbCommand(selectSql, accessConn))
            using (var reader = selectCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var values  = new object?[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                        values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    var rowKey  = BuildRowKeyFromValues(values, schema.PrimaryKeyColumns, colNames);
                    var rowHash = ComputeRowHashFromValues(values);
                    rows.Add((rowKey, rowHash, values));
                }
            }
            swRead.Stop();
            _logger.LogInformation("  ⏱ Access Read: {E:N1}s | {Count} שורות", swRead.Elapsed.TotalSeconds, rows.Count);

        } // חיבור Access נסגר כאן — לפני כל עבודת SQL Server
        swAccess.Stop();
        _logger.LogInformation("  ⏱ Access סה״כ (Open+Read): {E:N1}s", swAccess.Elapsed.TotalSeconds);

        // טען state רק לשורות שנמצאו (לא כל הטבלה); incremental PK → כולן חדשות → dict ריק מספיק
        var stateMap = windowDateColumn != null
            ? await _repo.GetSyncStateMapForKeysAsync(clientId, schema.TableName, rows.Select(r => r.rowKey))
            : await _repo.GetSyncStateMapAsync(clientId, schema.TableName);

        const int batchSize = 500;
        var pending = new List<(string rowKey, string rowHash, object?[] values, bool isInsert)>();

        foreach (var (rowKey, rowHash, values) in rows)
        {
            if (!stateMap.TryGetValue(rowKey, out var existingHash))
            {
                pending.Add((rowKey, rowHash, values, true));
                stateMap[rowKey] = rowHash;
                inserted++;
            }
            else if (existingHash != rowHash)
            {
                pending.Add((rowKey, rowHash, values, false));
                stateMap[rowKey] = rowHash;
                updated++;
            }
        }

        using var targetConn = new SqlConnection(_targetConnStr);
        targetConn.Open();

        // הכנס/עדכן ב-batches של 500 שורות
        for (int i = 0; i < pending.Count; i += batchSize)
        {
            var batch = pending.Skip(i).Take(batchSize).ToList();
            using (var tx = targetConn.BeginTransaction())
            {
                foreach (var (rowKey, rowHash, values, isInsert) in batch)
                    ExecuteCommandFromValues(targetConn, tx, isInsert ? insertSql : updateSql, colNames, values, rowHash);
                tx.Commit();
            }
            foreach (var (rowKey, rowHash, _, _) in batch)
                await _repo.UpsertSyncStateAsync(clientId, schema.TableName, rowKey, rowHash);
        }

        return (inserted, updated);
    }

    private async Task<(int inserted, int updated)> FullReplaceTableAsync(TableSchema schema)
    {
        var colNames  = schema.Columns.Select(c => c.Name).ToList();
        var paramList = string.Join(", ", Enumerable.Range(0, colNames.Count).Select(i => $"@p{i}"));
        var colList   = string.Join(", ", colNames.Select(c => $"[{c}]"));
        var insertSql = $"INSERT INTO [{schema.TableName}] ({colList}) VALUES ({paramList})";

        var rows = new List<object?[]>();
        using (var accessConn = new OleDbConnection(_accessConnStr))
        {
            accessConn.Open();
            using var selectCmd = new OleDbCommand($"SELECT * FROM [{schema.TableName}]", accessConn);
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(values);
            }
        } // חיבור Access נסגר לפני SQL Server

        using var targetConn = new SqlConnection(_targetConnStr);
        targetConn.Open();

        using (var tx = targetConn.BeginTransaction())
        {
            using var truncCmd = new SqlCommand($"TRUNCATE TABLE [{schema.TableName}]", targetConn, tx);
            truncCmd.ExecuteNonQuery();

            foreach (var values in rows)
            {
                using var cmd = new SqlCommand(insertSql, targetConn, tx);
                for (int i = 0; i < colNames.Count; i++)
                {
                    var raw = values[i];
                    if (raw is byte[] b) raw = Convert.ToHexString(b);
                    cmd.Parameters.AddWithValue($"@p{i}", raw ?? DBNull.Value);
                }
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        _logger.LogInformation("  [{Table}] Full Replace: {Count} שורות", schema.TableName, rows.Count);
        return await Task.FromResult((rows.Count, 0));
    }

    private static string BuildInsertSql(string tableName, List<string> cols)
    {
        var colList   = string.Join(", ", cols.Select(c => $"[{c}]")) + ", [RowHash]";
        var paramList = string.Join(", ", Enumerable.Range(0, cols.Count).Select(i => $"@p{i}")) + ", @pHash";
        return $"INSERT INTO [{tableName}] ({colList}) VALUES ({paramList})";
    }

    private static string BuildUpdateSql(string tableName, List<string> cols, List<string> pkCols)
    {
        var setClauses = new List<string>();
        for (int i = 0; i < cols.Count; i++)
        {
            if (!pkCols.Contains(cols[i], StringComparer.OrdinalIgnoreCase))
                setClauses.Add($"[{cols[i]}] = @p{i}");
        }
        setClauses.Add("[RowHash] = @pHash");

        var whereClauses = pkCols.Select(pk =>
        {
            var idx = cols.FindIndex(c => c.Equals(pk, StringComparison.OrdinalIgnoreCase));
            return $"[{pk}] = @p{idx}";
        });

        return $"UPDATE [{tableName}] SET {string.Join(", ", setClauses)} WHERE {string.Join(" AND ", whereClauses)}";
    }

    private static void ExecuteCommandFromValues(SqlConnection conn, SqlTransaction tx, string sql,
        List<string> colNames, object?[] values, string rowHash)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        for (int i = 0; i < colNames.Count; i++)
        {
            var val = values[i];
            if (val is byte[] b) val = Convert.ToHexString(b);
            cmd.Parameters.AddWithValue($"@p{i}", val ?? DBNull.Value);
        }
        cmd.Parameters.AddWithValue("@pHash", rowHash);
        cmd.ExecuteNonQuery();
    }

    private static string BuildRowKeyFromValues(object?[] values, List<string> pkCols, List<string> allCols)
    {
        var parts = pkCols.Select(pk =>
        {
            var idx = allCols.FindIndex(c => c.Equals(pk, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 && values[idx] != null ? ValueToString(values[idx]!) : "";
        });
        return string.Join("|", parts);
    }

    private static string ComputeRowHashFromValues(object?[] values)
    {
        var parts = values.Select(v => v == null ? "" : ValueToString(v));
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ValueToString(object value) => value switch
    {
        DateTime dt  => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        float    f   => f.ToString("R",   CultureInfo.InvariantCulture),
        double   d   => d.ToString("R",   CultureInfo.InvariantCulture),
        decimal  dec => dec.ToString(CultureInfo.InvariantCulture),
        bool     b   => b ? "1" : "0",
        byte[]   arr => Convert.ToHexString(arr),
        _            => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
}

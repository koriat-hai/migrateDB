using System.Data;
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
        var updateSql = BuildUpdateSql(schema.TableName, colNames, schema.PrimaryKeyColumns);

        int inserted = 0, updated = 0;

        var selectSql = windowDateColumn != null && windowStart.HasValue
            ? $"SELECT * FROM [{schema.TableName}] WHERE [{windowDateColumn}] >= #{windowStart.Value:yyyy-MM-dd}#"
            : $"SELECT * FROM [{schema.TableName}]";

        // קרא מ-Access לזיכרון
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
        }
        swAccess.Stop();
        _logger.LogInformation("  ⏱ Access סה״כ: {E:N1}s", swAccess.Elapsed.TotalSeconds);

        var stateMap = windowDateColumn != null
            ? await _repo.GetSyncStateMapForKeysAsync(clientId, schema.TableName, rows.Select(r => r.rowKey))
            : await _repo.GetSyncStateMapAsync(clientId, schema.TableName);

        var toInsert = new List<(string rowKey, string rowHash, object?[] values)>();
        var toUpdate = new List<(string rowKey, string rowHash, object?[] values)>();

        foreach (var (rowKey, rowHash, values) in rows)
        {
            if (!stateMap.TryGetValue(rowKey, out var existingHash))
            {
                toInsert.Add((rowKey, rowHash, values));
                inserted++;
            }
            else if (existingHash != rowHash)
            {
                toUpdate.Add((rowKey, rowHash, values));
                updated++;
            }
        }

        using var targetConn = new SqlConnection(_targetConnStr);
        targetConn.Open();

        // SqlBulkCopy → staging → MERGE לטיפול במקרה ש-SyncState לא מסונכרן עם הטבלה האמיתית
        if (toInsert.Count > 0)
        {
            var swBulk    = Stopwatch.StartNew();
            var dt        = BuildInsertDataTable(schema, colNames, toInsert);
            var stageName = "#stage" + Guid.NewGuid().ToString("N")[..8];

            // staging table בעלת אותה מבנה כמו הטבלה (כולל RowHash)
            new SqlCommand($"SELECT TOP 0 * INTO [{stageName}] FROM [{schema.TableName}]", targetConn)
                .ExecuteNonQuery();

            using (var bulkCopy = new SqlBulkCopy(targetConn) { DestinationTableName = stageName, BatchSize = 5000, BulkCopyTimeout = 0 })
            {
                foreach (DataColumn col in dt.Columns)
                    bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                bulkCopy.WriteToServer(dt);
            }

            var pkMatch    = string.Join(" AND ", schema.PrimaryKeyColumns.Select(pk => $"t.[{pk}] = s.[{pk}]"));
            var allCols    = colNames.Append("RowHash").ToList();
            var nonPkCols  = allCols.Where(c => !schema.PrimaryKeyColumns.Contains(c, StringComparer.OrdinalIgnoreCase));
            var setClause  = string.Join(", ", nonPkCols.Select(c => $"t.[{c}] = s.[{c}]"));
            var insCols    = string.Join(", ", allCols.Select(c => $"[{c}]"));
            var insVals    = string.Join(", ", allCols.Select(c => $"s.[{c}]"));

            var mergeSql = $"MERGE [{schema.TableName}] AS t USING [{stageName}] AS s ON {pkMatch} " +
                           $"WHEN MATCHED THEN UPDATE SET {setClause} " +
                           $"WHEN NOT MATCHED THEN INSERT ({insCols}) VALUES ({insVals});";

            using var mergeCmd = new SqlCommand(mergeSql, targetConn) { CommandTimeout = 0 };
            mergeCmd.ExecuteNonQuery();

            new SqlCommand($"DROP TABLE [{stageName}]", targetConn).ExecuteNonQuery();

            swBulk.Stop();
            _logger.LogInformation("  ⏱ BulkInsert+Merge {Count} שורות: {E:N1}s", toInsert.Count, swBulk.Elapsed.TotalSeconds);
        }

        // UPDATEs — בד"כ מעטים, נשארים row-by-row בתוך batches
        if (toUpdate.Count > 0)
        {
            const int batchSize = 500;
            var swUpd = Stopwatch.StartNew();
            for (int i = 0; i < toUpdate.Count; i += batchSize)
            {
                var batch = toUpdate.Skip(i).Take(batchSize).ToList();
                using var tx = targetConn.BeginTransaction();
                foreach (var (_, rowHash, values) in batch)
                    ExecuteCommandFromValues(targetConn, tx, updateSql, colNames, values, rowHash);
                tx.Commit();
            }
            swUpd.Stop();
            _logger.LogInformation("  ⏱ Updates {Count} שורות: {E:N1}s", toUpdate.Count, swUpd.Elapsed.TotalSeconds);
        }

        // עדכון SyncState — bulk upsert אחד לכל הטבלה
        var allChanged = toInsert.Select(r => (r.rowKey, r.rowHash))
            .Concat(toUpdate.Select(r => (r.rowKey, r.rowHash)))
            .ToList();

        if (allChanged.Count > 0)
        {
            var swState = Stopwatch.StartNew();
            await _repo.BulkUpsertSyncStateAsync(clientId, schema.TableName, allChanged);
            swState.Stop();
            _logger.LogInformation("  ⏱ SyncState bulk upsert {Count}: {E:N1}s", allChanged.Count, swState.Elapsed.TotalSeconds);
        }

        return (inserted, updated);
    }

    private async Task<(int inserted, int updated)> FullReplaceTableAsync(TableSchema schema)
    {
        var colNames = schema.Columns.Select(c => c.Name).ToList();

        var dt = new DataTable();
        foreach (var col in schema.Columns)
            dt.Columns.Add(col.Name, typeof(object));

        using (var accessConn = new OleDbConnection(_accessConnStr))
        {
            accessConn.Open();
            using var selectCmd = new OleDbCommand($"SELECT * FROM [{schema.TableName}]", accessConn);
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                var row = dt.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var val = reader.IsDBNull(i) ? (object?)null : reader.GetValue(i);
                    if (val is byte[] b) val = Convert.ToHexString(b);
                    row[colNames[i]] = val ?? DBNull.Value;
                }
                dt.Rows.Add(row);
            }
        }

        using var targetConn = new SqlConnection(_targetConnStr);
        targetConn.Open();

        using (var tx = targetConn.BeginTransaction())
        {
            new SqlCommand($"TRUNCATE TABLE [{schema.TableName}]", targetConn, tx).ExecuteNonQuery();
            tx.Commit();
        }

        using (var bulkCopy = new SqlBulkCopy(targetConn)
        {
            DestinationTableName = schema.TableName,
            BatchSize            = 5000,
            BulkCopyTimeout      = 0
        })
        {
            foreach (DataColumn col in dt.Columns)
                bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            bulkCopy.WriteToServer(dt);
        }

        _logger.LogInformation("  [{Table}] Full Replace BulkCopy: {Count} שורות", schema.TableName, dt.Rows.Count);
        return (dt.Rows.Count, 0);
    }

    private static DataTable BuildInsertDataTable(TableSchema schema, List<string> colNames,
        List<(string rowKey, string rowHash, object?[] values)> rows)
    {
        var dt = new DataTable();
        foreach (var col in schema.Columns)
            dt.Columns.Add(col.Name, typeof(object));
        dt.Columns.Add("RowHash", typeof(string));

        foreach (var (_, rowHash, values) in rows)
        {
            var row = dt.NewRow();
            for (int i = 0; i < colNames.Count; i++)
            {
                var val = values[i];
                if (val is byte[] b) val = Convert.ToHexString(b);
                row[colNames[i]] = val ?? DBNull.Value;
            }
            row["RowHash"] = rowHash;
            dt.Rows.Add(row);
        }

        return dt;
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

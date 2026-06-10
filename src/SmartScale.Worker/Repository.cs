using Dapper;
using Microsoft.Data.SqlClient;

namespace SmartScale.Worker;

public class MigrateDbRepository
{
    private readonly string _connectionString;

    public MigrateDbRepository(string connectionString) => _connectionString = connectionString;

    private SqlConnection Open() => new(_connectionString);

    // ── Clients ──────────────────────────────────────────────────────────────

    public async Task<IEnumerable<Client>> GetActiveClientsAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<Client>("SELECT * FROM Clients WHERE IsActive = 1");
    }

    public async Task ClearRunRequestedAsync(int clientId)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE Clients SET RunRequestedAt = NULL WHERE ClientId = @Id",
            new { Id = clientId });
    }

    // ── SyncLog ──────────────────────────────────────────────────────────────

    public async Task AddSyncLogAsync(SyncLog log)
    {
        using var conn = Open();
        await conn.ExecuteAsync(@"
            INSERT INTO SyncLog
                (ClientId, RunStartedAt, RunFinishedAt, TableName, InsertCount, UpdateCount, Status, Message)
            VALUES
                (@ClientId, @RunStartedAt, @RunFinishedAt, @TableName, @InsertCount, @UpdateCount, @Status, @Message)",
            log);
    }

    public async Task DeleteOldLogsAsync()
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "DELETE FROM SyncLog WHERE RunStartedAt < @Cutoff",
            new { Cutoff = DateTime.UtcNow.AddHours(-48) });
    }

    // ── GlobalSettings ────────────────────────────────────────────────────────

    public async Task<int> GetGlobalSettingIntAsync(string key, int defaultValue)
    {
        using var conn = Open();
        var val = await conn.ExecuteScalarAsync<string>(
            "SELECT SettingValue FROM GlobalSettings WHERE SettingKey = @Key",
            new { Key = key });
        return val != null && int.TryParse(val, out var n) ? n : defaultValue;
    }

    // ── SyncState ─────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, string>> GetSyncStateMapAsync(int clientId, string tableName)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<StateRow>(
            "SELECT RowKey, RowHash FROM SyncState WHERE ClientId = @ClientId AND TableName = @TableName",
            new { ClientId = clientId, TableName = tableName });
        return rows.ToDictionary(r => r.RowKey, r => r.RowHash);
    }

    public async Task<Dictionary<string, string>> GetSyncStateMapForKeysAsync(int clientId, string tableName, IEnumerable<string> rowKeys)
    {
        var keyList = rowKeys.ToList();
        if (keyList.Count == 0) return new Dictionary<string, string>();

        // SQL Server מגביל 2100 פרמטרים — חלק לחבילות של 2000
        const int chunkSize = 2000;
        var result = new Dictionary<string, string>();
        using var conn = Open();
        for (int i = 0; i < keyList.Count; i += chunkSize)
        {
            var chunk = keyList.Skip(i).Take(chunkSize).ToList();
            var rows = await conn.QueryAsync<StateRow>(
                "SELECT RowKey, RowHash FROM SyncState WHERE ClientId = @ClientId AND TableName = @TableName AND RowKey IN @RowKeys",
                new { ClientId = clientId, TableName = tableName, RowKeys = chunk });
            foreach (var r in rows)
                result[r.RowKey] = r.RowHash;
        }
        return result;
    }

    public async Task UpsertSyncStateAsync(int clientId, string tableName, string rowKey, string rowHash)
    {
        using var conn = Open();
        await conn.ExecuteAsync(@"
            MERGE SyncState AS target
            USING (SELECT @ClientId AS ClientId, @TableName AS TableName, @RowKey AS RowKey) AS src
                ON  target.ClientId  = src.ClientId
                AND target.TableName = src.TableName
                AND target.RowKey    = src.RowKey
            WHEN MATCHED THEN
                UPDATE SET RowHash = @RowHash, UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (ClientId, TableName, RowKey, RowHash, UpdatedAt)
                VALUES (@ClientId, @TableName, @RowKey, @RowHash, GETUTCDATE());",
            new { ClientId = clientId, TableName = tableName, RowKey = rowKey, RowHash = rowHash });
    }

    // ── Meta (file mtime tracking) ─────────────────────────────────────────────

    public async Task<string?> GetMetaValueAsync(int clientId, string key)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT RowHash FROM SyncState WHERE ClientId = @ClientId AND TableName = '__meta__' AND RowKey = @Key",
            new { ClientId = clientId, Key = key });
    }

    public async Task SetMetaValueAsync(int clientId, string key, string value)
    {
        using var conn = Open();
        await conn.ExecuteAsync(@"
            MERGE SyncState AS target
            USING (SELECT @ClientId AS ClientId, '__meta__' AS TableName, @Key AS RowKey) AS src
                ON  target.ClientId  = src.ClientId
                AND target.TableName = src.TableName
                AND target.RowKey    = src.RowKey
            WHEN MATCHED THEN
                UPDATE SET RowHash = @Value, UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (ClientId, TableName, RowKey, RowHash, UpdatedAt)
                VALUES (@ClientId, '__meta__', @Key, @Value, GETUTCDATE());",
            new { ClientId = clientId, Key = key, Value = value });
    }

    private class StateRow
    {
        public string RowKey  { get; set; } = "";
        public string RowHash { get; set; } = "";
    }
}

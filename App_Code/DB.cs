using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Web.Configuration;

// ─── Models ──────────────────────────────────────────────────────────────────

public class Client
{
    public int       ClientId         { get; set; }
    public string    ClientName       { get; set; }
    public string    AccessFolderPath { get; set; }
    public string    TargetDatabase   { get; set; }
    public bool      IsActive         { get; set; }
    public DateTime? RunRequestedAt   { get; set; }
    public DateTime  CreatedAt        { get; set; }
    public DateTime  UpdatedAt        { get; set; }
}

public class SyncLog
{
    public int       LogId         { get; set; }
    public int       ClientId      { get; set; }
    public string    ClientName    { get; set; }
    public DateTime  RunStartedAt  { get; set; }
    public DateTime? RunFinishedAt { get; set; }
    public string    TableName     { get; set; }
    public int       InsertCount   { get; set; }
    public int       UpdateCount   { get; set; }
    public string    Status        { get; set; }
    public string    Message       { get; set; }
}

// ─── Database helper ─────────────────────────────────────────────────────────

public static class DB
{
    private static string ConnStr
    {
        get { return WebConfigurationManager.ConnectionStrings["MigrateDb"].ConnectionString; }
    }

    private static SqlConnection Open()
    {
        var conn = new SqlConnection(ConnStr);
        conn.Open();
        return conn;
    }

    // ── Clients ──────────────────────────────────────────────────────────────

    public static List<Client> GetClients()
    {
        var list = new List<Client>();
        using (var conn = Open())
        using (var cmd = new SqlCommand("SELECT * FROM Clients ORDER BY ClientName", conn))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) list.Add(ReadClient(r));
        return list;
    }

    public static Client GetClient(int id)
    {
        using (var conn = Open())
        using (var cmd = new SqlCommand("SELECT * FROM Clients WHERE ClientId = @Id", conn))
        {
            cmd.Parameters.AddWithValue("@Id", id);
            using (var r = cmd.ExecuteReader())
                return r.Read() ? ReadClient(r) : null;
        }
    }

    public static int AddClient(string name, string path, string db, bool active)
    {
        using (var conn = Open())
        using (var cmd = new SqlCommand(@"
            INSERT INTO Clients (ClientName, AccessFolderPath, TargetDatabase, IsActive, CreatedAt, UpdatedAt)
            OUTPUT INSERTED.ClientId
            VALUES (@n, @p, @d, @a, GETUTCDATE(), GETUTCDATE())", conn))
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@p", path);
            cmd.Parameters.AddWithValue("@d", db);
            cmd.Parameters.AddWithValue("@a", active);
            return (int)cmd.ExecuteScalar();
        }
    }

    public static void UpdateClient(int id, string name, string path, string db, bool active)
    {
        using (var conn = Open())
        using (var cmd = new SqlCommand(@"
            UPDATE Clients
            SET ClientName=@n, AccessFolderPath=@p, TargetDatabase=@d,
                IsActive=@a, UpdatedAt=GETUTCDATE()
            WHERE ClientId=@id", conn))
        {
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@n",  name);
            cmd.Parameters.AddWithValue("@p",  path);
            cmd.Parameters.AddWithValue("@d",  db);
            cmd.Parameters.AddWithValue("@a",  active);
            cmd.ExecuteNonQuery();
        }
    }

    public static void DeleteClient(int id)
    {
        using (var conn = Open())
        {
            foreach (var sql in new[] {
                "DELETE FROM SyncState WHERE ClientId=@Id",
                "DELETE FROM SyncLog   WHERE ClientId=@Id",
                "DELETE FROM Clients   WHERE ClientId=@Id" })
            {
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }

    public static void ToggleActive(int id)
    {
        using (var conn = Open())
        using (var cmd = new SqlCommand(@"
            UPDATE Clients
            SET IsActive = CASE WHEN IsActive=1 THEN 0 ELSE 1 END, UpdatedAt=GETUTCDATE()
            WHERE ClientId=@Id", conn))
        {
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public static void SetRunRequested(int id)
    {
        using (var conn = Open())
        using (var cmd = new SqlCommand(
            "UPDATE Clients SET RunRequestedAt=GETUTCDATE() WHERE ClientId=@Id", conn))
        {
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public static void ClearClientMtime(int id)
    {
        using (var conn = Open())
        using (var cmd = new SqlCommand(
            "DELETE FROM SyncState WHERE ClientId=@Id AND TableName='__meta__'", conn))
        {
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ── SyncLog ──────────────────────────────────────────────────────────────

    public static List<SyncLog> GetLogs(int? clientId = null, int limit = 300)
    {
        var list = new List<SyncLog>();
        var sql = clientId.HasValue
            ? string.Format(@"SELECT TOP {0} l.*, c.ClientName
                              FROM SyncLog l JOIN Clients c ON l.ClientId=c.ClientId
                              WHERE l.ClientId=@cid ORDER BY l.RunStartedAt DESC", limit)
            : string.Format(@"SELECT TOP {0} l.*, c.ClientName
                              FROM SyncLog l JOIN Clients c ON l.ClientId=c.ClientId
                              ORDER BY l.RunStartedAt DESC", limit);
        using (var conn = Open())
        using (var cmd = new SqlCommand(sql, conn))
        {
            if (clientId.HasValue) cmd.Parameters.AddWithValue("@cid", clientId.Value);
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(ReadLog(r));
        }
        return list;
    }

    public static Dictionary<int, SyncLog> GetLastRunPerClient()
    {
        var dict = new Dictionary<int, SyncLog>();
        using (var conn = Open())
        using (var cmd = new SqlCommand(@"
            SELECT l.* FROM SyncLog l
            INNER JOIN (
                SELECT ClientId, MAX(LogId) AS MaxId FROM SyncLog GROUP BY ClientId
            ) m ON l.LogId = m.MaxId", conn))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var log = ReadLog(r);
                dict[log.ClientId] = log;
            }
        return dict;
    }

    // ── Private readers ───────────────────────────────────────────────────────

    private static Client ReadClient(SqlDataReader r)
    {
        return new Client
        {
            ClientId         = (int)r["ClientId"],
            ClientName       = r["ClientName"].ToString(),
            AccessFolderPath = r["AccessFolderPath"].ToString(),
            TargetDatabase   = r["TargetDatabase"].ToString(),
            IsActive         = (bool)r["IsActive"],
            RunRequestedAt   = r["RunRequestedAt"] is DBNull ? (DateTime?)null : (DateTime)r["RunRequestedAt"],
            CreatedAt        = (DateTime)r["CreatedAt"],
            UpdatedAt        = (DateTime)r["UpdatedAt"]
        };
    }

    private static SyncLog ReadLog(SqlDataReader r)
    {
        return new SyncLog
        {
            LogId         = (int)r["LogId"],
            ClientId      = (int)r["ClientId"],
            ClientName    = HasColumn(r, "ClientName") ? r["ClientName"].ToString() : null,
            RunStartedAt  = (DateTime)r["RunStartedAt"],
            RunFinishedAt = r["RunFinishedAt"] is DBNull ? (DateTime?)null : (DateTime)r["RunFinishedAt"],
            TableName     = r["TableName"].ToString(),
            InsertCount   = (int)r["InsertCount"],
            UpdateCount   = (int)r["UpdateCount"],
            Status        = r["Status"].ToString(),
            Message       = r["Message"] is DBNull ? null : r["Message"].ToString()
        };
    }

    private static bool HasColumn(SqlDataReader r, string col)
    {
        for (int i = 0; i < r.FieldCount; i++)
        {
            if (string.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

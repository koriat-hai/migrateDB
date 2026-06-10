namespace SmartScale.Worker;

public class Client
{
    public int      ClientId         { get; set; }
    public string   ClientName       { get; set; } = "";
    public string   AccessFolderPath { get; set; } = "";
    public string   TargetDatabase   { get; set; } = "";
    public bool     IsActive         { get; set; } = true;
    public DateTime? RunRequestedAt  { get; set; }
    public DateTime CreatedAt        { get; set; }
    public DateTime UpdatedAt        { get; set; }
}

public class SyncLog
{
    public int      LogId         { get; set; }
    public int      ClientId      { get; set; }
    public string?  ClientName    { get; set; }
    public DateTime RunStartedAt  { get; set; }
    public DateTime? RunFinishedAt { get; set; }
    public string   TableName     { get; set; } = "";
    public int      InsertCount   { get; set; }
    public int      UpdateCount   { get; set; }
    public string   Status        { get; set; } = "";
    public string?  Message       { get; set; }
}

public class SyncState
{
    public int      StateId   { get; set; }
    public int      ClientId  { get; set; }
    public string   TableName { get; set; } = "";
    public string   RowKey    { get; set; } = "";
    public string   RowHash   { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

-- סקריפט יצירת טבלאות ב-migrateDB
-- הרץ אחרי יצירת ה-DB הריק: CREATE DATABASE migrateDB

USE migrateDB;
GO

-- ----------------------------------------------------------------
-- טבלת לקוחות
-- ----------------------------------------------------------------
CREATE TABLE Clients (
    ClientId          INT            IDENTITY(1,1) PRIMARY KEY,
    ClientName        NVARCHAR(255)  NOT NULL,
    AccessFolderPath  NVARCHAR(1000) NOT NULL,           -- נתיב לתיקייה המכילה את ה-.accdb
    TargetDatabase    NVARCHAR(255)  NOT NULL,            -- שם ה-DB ב-SQL Server היעד
    IsActive          BIT            NOT NULL DEFAULT 1,
    RunRequestedAt    DATETIME2      NULL,                -- מסומן ע"י ה-GUI ל"הרץ עכשיו"
    CreatedAt         DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
    UpdatedAt         DATETIME2      NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ----------------------------------------------------------------
-- לוג ריצות סנכרון
-- ----------------------------------------------------------------
CREATE TABLE SyncLog (
    LogId          INT            IDENTITY(1,1) PRIMARY KEY,
    ClientId       INT            NOT NULL,
    RunStartedAt   DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
    RunFinishedAt  DATETIME2      NULL,
    TableName      NVARCHAR(255)  NOT NULL,
    InsertCount    INT            NOT NULL DEFAULT 0,
    UpdateCount    INT            NOT NULL DEFAULT 0,
    Status         NVARCHAR(50)   NOT NULL,   -- Success / Error / Skipped
    Message        NVARCHAR(MAX)  NULL,
    CONSTRAINT FK_SyncLog_Clients FOREIGN KEY (ClientId) REFERENCES Clients(ClientId)
);
GO

CREATE INDEX IX_SyncLog_Client ON SyncLog (ClientId, RunStartedAt DESC);
GO

-- ----------------------------------------------------------------
-- מצב סנכרון (hash לכל שורה — לזיהוי שינויים)
-- ----------------------------------------------------------------
CREATE TABLE SyncState (
    StateId    INT            IDENTITY(1,1) PRIMARY KEY,
    ClientId   INT            NOT NULL,
    TableName  NVARCHAR(255)  NOT NULL,
    RowKey     NVARCHAR(500)  NOT NULL,   -- ערכי ה-PK מאוחד ב-|
    RowHash    CHAR(32)       NOT NULL,   -- MD5 hex של כל ערכי השורה
    UpdatedAt  DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_SyncState_Clients FOREIGN KEY (ClientId) REFERENCES Clients(ClientId)
);
GO

CREATE UNIQUE INDEX IX_SyncState_Lookup ON SyncState (ClientId, TableName, RowKey);
GO

-- ----------------------------------------------------------------
-- הגדרות גלובליות
-- ----------------------------------------------------------------
CREATE TABLE GlobalSettings (
    SettingKey    NVARCHAR(100)  NOT NULL PRIMARY KEY,
    SettingValue  NVARCHAR(500)  NOT NULL,
    Description   NVARCHAR(500)  NULL
);
GO

INSERT INTO GlobalSettings (SettingKey, SettingValue, Description) VALUES
    ('MaxConcurrentClients', '10', 'מספר לקוחות מקסימלי שיסתנכרנו במקביל');
GO

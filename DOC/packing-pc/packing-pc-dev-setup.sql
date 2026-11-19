/* CMC_Logiwa DEV database for the PACKING PC (one script).
   Creates Local_CMC_Logiwa_DEV, the login CMC_Logiwa_app_dev, tables, procedures, machine event codes and 3 TEST cartons.
   BEFORE running: replace CHANGE_ME_BEFORE_RUNNING (once, on the DECLARE line near the middle) with the password you choose
   for the login CMC_Logiwa_app_dev. Run as a sysadmin in SSMS (or sqlcmd with -I). Safe to run again.
   It does not touch any other database (for example V1's CMC). */
GO
-- ===== 1. schema =====
/*
  CMC_Logiwa local database - schema (DEV)
  Database: Local_CMC_Logiwa_DEV  (PRO later: same script with the database name changed)
  Safe to re-run: every object is created only if missing.
  Run with an admin login in SSMS on the packing PC SQL instance (CMC_Control\CMC_Control).
*/
IF DB_ID(N'Local_CMC_Logiwa_DEV') IS NULL
    CREATE DATABASE [Local_CMC_Logiwa_DEV];
GO
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------------------------------------------------------------- Users */
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
CREATE TABLE dbo.Users(
    UserId        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Username      nvarchar(50)  NOT NULL,
    PasswordHash  varbinary(64) NOT NULL,   -- PBKDF2 (done in the app)
    Salt          varbinary(32) NOT NULL,
    Iterations    int           NOT NULL,
    IsActive      bit           NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
    CreatedAt     datetime2(0)  NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT (SYSDATETIME()),
    LastLoginAt   datetime2(0)  NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Users_Username' AND object_id = OBJECT_ID(N'dbo.Users'))
    CREATE UNIQUE INDEX UX_Users_Username ON dbo.Users(Username);
GO

/* ------------------------------------------------------------- PackJob
   1 tracking number = 1 box = 1 label = 1 current record.
   V1 column names kept (typos fixed: BoxSzieL -> BoxSizeL).
   Units as received from CMC: sizes in mm, weights in g.                    */
IF OBJECT_ID(N'dbo.PackJob', N'U') IS NULL
CREATE TABLE dbo.PackJob(
    PackJobID               int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PackJob PRIMARY KEY,
    JobCreatedDateTime      datetime2(0)  NOT NULL CONSTRAINT DF_PackJob_Created DEFAULT (SYSDATETIME()),
    JobNumber               nvarchar(50)  NULL,      -- = tracking number
    OrderNumber             nvarchar(50)  NOT NULL,  -- = tracking number (scanned value)
    MatchLab                nvarchar(100) NULL,      -- = tracking number (CMC compares it to the printed label)
    ShipmentIdentifier      nvarchar(64)  NULL,      -- Logiwa unique order id
    OrderCode               nvarchar(100) NULL,      -- client order number
    ClientCode              nvarchar(50)  NULL,
    LabelData               nvarchar(max) NULL,      -- raw ZPL
    LabelDataLen            int           NULL,
    OrderStatus             nvarchar(20)  NOT NULL,  -- NoLabel, Ready, InProgress, Completed, Failed, Removed
    ProcessStartedDateTime  datetime2(0)  NULL,
    ProcessCompletedDateTime datetime2(0) NULL,
    PackID                  nvarchar(20)  NULL,
    BoxEventID              nvarchar(20)  NULL,
    BoxEventText            nvarchar(100) NULL,
    FlagBoxLow              nvarchar(5)   NULL,
    HasBoxIssue             bit           NULL,
    BoxIssueComment         nvarchar(300) NULL,
    BoxIssueDateTime        datetime2(0)  NULL,
    BoxSizeH                int           NULL,      -- mm
    BoxSizeL                int           NULL,      -- mm
    BoxSizeW                int           NULL,      -- mm
    BoxArea                 int           NULL,
    WeightCarton            int           NULL,      -- g
    WeightScale             int           NULL,      -- g (this one is sent to Logiwa, converted to kg on the web VM)
    MachineId               nvarchar(20)  NULL,
    ProcessedByUser         nvarchar(50)  NULL,
    SyncSeq                 bigint        NULL,      -- web VM change counter at last download
    IsCurrent               bit           NOT NULL CONSTRAINT DF_PackJob_IsCurrent DEFAULT (1),
    ResetFromPackJobID      int           NULL,
    ResetByUser             nvarchar(50)  NULL,
    ResetAt                 datetime2(0)  NULL,
    PushStatus              nvarchar(10)  NOT NULL CONSTRAINT DF_PackJob_PushStatus DEFAULT (N'None'),  -- None, Pending, Sent
    PushAttempts            int           NOT NULL CONSTRAINT DF_PackJob_PushAttempts DEFAULT (0),
    PushNextAt              datetime2(0)  NULL,
    PushLastError           nvarchar(400) NULL,
    PushedAt                datetime2(0)  NULL,
    CONSTRAINT CK_PackJob_Status CHECK (OrderStatus IN (N'NoLabel', N'Ready', N'InProgress', N'Completed', N'Failed', N'Removed')),
    CONSTRAINT CK_PackJob_Push   CHECK (PushStatus  IN (N'None', N'Pending', N'Sent'))
);
GO
-- exactly one current row per tracking number
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PackJob_OrderNumber_Current' AND object_id = OBJECT_ID(N'dbo.PackJob'))
    CREATE UNIQUE INDEX UX_PackJob_OrderNumber_Current ON dbo.PackJob(OrderNumber) WHERE IsCurrent = 1;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PackJob_OrderNumber' AND object_id = OBJECT_ID(N'dbo.PackJob'))
    CREATE INDEX IX_PackJob_OrderNumber ON dbo.PackJob(OrderNumber);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PackJob_Push' AND object_id = OBJECT_ID(N'dbo.PackJob'))
    CREATE INDEX IX_PackJob_Push ON dbo.PackJob(PushStatus, PushNextAt) WHERE PushStatus = N'Pending';
GO

/* ------------------------------------------------------ PackJobArchive
   Same columns as PackJob (no identity) plus ArchivedAt. Nothing is deleted for good. */
IF OBJECT_ID(N'dbo.PackJobArchive', N'U') IS NULL
CREATE TABLE dbo.PackJobArchive(
    PackJobID               int           NOT NULL CONSTRAINT PK_PackJobArchive PRIMARY KEY,
    JobCreatedDateTime      datetime2(0)  NOT NULL,
    JobNumber               nvarchar(50)  NULL,
    OrderNumber             nvarchar(50)  NOT NULL,
    MatchLab                nvarchar(100) NULL,
    ShipmentIdentifier      nvarchar(64)  NULL,
    OrderCode               nvarchar(100) NULL,
    ClientCode              nvarchar(50)  NULL,
    LabelData               nvarchar(max) NULL,
    LabelDataLen            int           NULL,
    OrderStatus             nvarchar(20)  NOT NULL,
    ProcessStartedDateTime  datetime2(0)  NULL,
    ProcessCompletedDateTime datetime2(0) NULL,
    PackID                  nvarchar(20)  NULL,
    BoxEventID              nvarchar(20)  NULL,
    BoxEventText            nvarchar(100) NULL,
    FlagBoxLow              nvarchar(5)   NULL,
    HasBoxIssue             bit           NULL,
    BoxIssueComment         nvarchar(300) NULL,
    BoxIssueDateTime        datetime2(0)  NULL,
    BoxSizeH                int           NULL,
    BoxSizeL                int           NULL,
    BoxSizeW                int           NULL,
    BoxArea                 int           NULL,
    WeightCarton            int           NULL,
    WeightScale             int           NULL,
    MachineId               nvarchar(20)  NULL,
    ProcessedByUser         nvarchar(50)  NULL,
    SyncSeq                 bigint        NULL,
    IsCurrent               bit           NOT NULL,
    ResetFromPackJobID      int           NULL,
    ResetByUser             nvarchar(50)  NULL,
    ResetAt                 datetime2(0)  NULL,
    PushStatus              nvarchar(10)  NOT NULL,
    PushAttempts            int           NOT NULL,
    PushNextAt              datetime2(0)  NULL,
    PushLastError           nvarchar(400) NULL,
    PushedAt                datetime2(0)  NULL,
    ArchivedAt              datetime2(0)  NOT NULL CONSTRAINT DF_PackJobArchive_At DEFAULT (SYSDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PackJobArchive_OrderNumber' AND object_id = OBJECT_ID(N'dbo.PackJobArchive'))
    CREATE INDEX IX_PackJobArchive_OrderNumber ON dbo.PackJobArchive(OrderNumber);
GO

/* ---------------------------------------------------------- PackJobEvent (audit trail) */
IF OBJECT_ID(N'dbo.PackJobEvent', N'U') IS NULL
CREATE TABLE dbo.PackJobEvent(
    EventId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PackJobEvent PRIMARY KEY,
    PackJobID    int           NULL,
    OrderNumber  nvarchar(50)  NULL,
    EventType    nvarchar(20)  NOT NULL,   -- Scan, NotFound, Duplicate, NoLabel, Reject, Reset, ACK, PRN, LAB, END, REM, Archive ...
    EventAt      datetime2(3)  NOT NULL CONSTRAINT DF_PackJobEvent_At DEFAULT (SYSDATETIME()),
    UserName     nvarchar(50)  NULL,
    MachineId    nvarchar(20)  NULL,
    Detail       nvarchar(400) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PackJobEvent_Order' AND object_id = OBJECT_ID(N'dbo.PackJobEvent'))
    CREATE INDEX IX_PackJobEvent_Order ON dbo.PackJobEvent(OrderNumber, EventAt);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PackJobEvent_Type' AND object_id = OBJECT_ID(N'dbo.PackJobEvent'))
    CREATE INDEX IX_PackJobEvent_Type ON dbo.PackJobEvent(EventType, EventAt);
GO
IF OBJECT_ID(N'dbo.PackJobEventArchive', N'U') IS NULL
CREATE TABLE dbo.PackJobEventArchive(
    EventId      bigint        NOT NULL CONSTRAINT PK_PackJobEventArchive PRIMARY KEY,
    PackJobID    int           NULL,
    OrderNumber  nvarchar(50)  NULL,
    EventType    nvarchar(20)  NOT NULL,
    EventAt      datetime2(3)  NOT NULL,
    UserName     nvarchar(50)  NULL,
    MachineId    nvarchar(20)  NULL,
    Detail       nvarchar(400) NULL
);
GO

/* ------------------------------------------------------------ LabelRequest
   "No label" on a scan -> one request is sent to the web VM (never more than one open per tracking number). */
IF OBJECT_ID(N'dbo.LabelRequest', N'U') IS NULL
CREATE TABLE dbo.LabelRequest(
    RequestId       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabelRequest PRIMARY KEY,
    OrderNumber     nvarchar(50)  NOT NULL,
    MachineId       nvarchar(20)  NULL,
    RequestedAt     datetime2(0)  NOT NULL CONSTRAINT DF_LabelRequest_At DEFAULT (SYSDATETIME()),
    Status          nvarchar(10)  NOT NULL CONSTRAINT DF_LabelRequest_Status DEFAULT (N'Pending'),  -- Pending, Sent, Done
    Attempts        int           NOT NULL CONSTRAINT DF_LabelRequest_Attempts DEFAULT (0),
    NextAttemptAt   datetime2(0)  NULL,
    LastError       nvarchar(400) NULL,
    SentAt          datetime2(0)  NULL,
    CONSTRAINT CK_LabelRequest_Status CHECK (Status IN (N'Pending', N'Sent', N'Done'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LabelRequest_Order' AND object_id = OBJECT_ID(N'dbo.LabelRequest'))
    CREATE INDEX IX_LabelRequest_Order ON dbo.LabelRequest(OrderNumber, Status);
GO

/* ------------------------------------------------------------------ Events
   Machine event codes -> text (same idea as V1 dbo.Events). Filled from V1 with DOC\v1-events.sql. */
IF OBJECT_ID(N'dbo.Events', N'U') IS NULL
CREATE TABLE dbo.Events(
    EventCode  nvarchar(20)  NOT NULL CONSTRAINT PK_Events PRIMARY KEY,
    EventText  nvarchar(100) NULL
);
GO

/* ------------------------------------------------------------- SyncState (single row) */
IF OBJECT_ID(N'dbo.SyncState', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SyncState(
        Id            int NOT NULL CONSTRAINT PK_SyncState PRIMARY KEY CONSTRAINT CK_SyncState_One CHECK (Id = 1),
        LastSyncSeq   bigint       NOT NULL CONSTRAINT DF_SyncState_Seq DEFAULT (0),
        LastPollAt    datetime2(0) NULL,
        LastPollError nvarchar(400) NULL
    );
    INSERT dbo.SyncState(Id) VALUES (1);
END
GO
GO
-- ===== 2. application login =====
/*
  CMC_Logiwa - DEV application login
  Server: TAREK-ZAKI\TAREKSMART2025   Database: Local_CMC_Logiwa_DEV   Login: CMC_Logiwa_app_dev

  1. Run 001_schema_dev.sql first (it creates the database), then this script, then 002_procs_dev.sql.
  2. Run with an admin login (sysadmin) in SSMS or sqlcmd (-I).
  3. Replace the password below BEFORE running, and do not commit the edited file.
     (The script refuses to run while the placeholder is still there.)

  Permissions: read, write and EXECUTE on this one database only. No schema changes.
  PRO later: same script with Local_CMC_Logiwa_PRO and CMC_Logiwa_app_pro, on the production server.
*/
USE [master];
GO
DECLARE @pwd nvarchar(128) = N'CHANGE_ME_BEFORE_RUNNING';

IF @pwd = N'CHANGE_ME_' + N'BEFORE_RUNNING'
    THROW 50000, 'Edit the password in this script before running it.', 1;

IF SUSER_ID(N'CMC_Logiwa_app_dev') IS NULL
BEGIN
    DECLARE @sql nvarchar(max) =
        N'CREATE LOGIN [CMC_Logiwa_app_dev] WITH PASSWORD = N''' + REPLACE(@pwd, N'''', N'''''') +
        N''', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [Local_CMC_Logiwa_DEV];';
    EXEC (@sql);
    PRINT 'Login CMC_Logiwa_app_dev created.';
END
ELSE
    PRINT 'Login CMC_Logiwa_app_dev already exists (password not changed).';
GO

USE [Local_CMC_Logiwa_DEV];
GO
IF USER_ID(N'CMC_Logiwa_app_dev') IS NULL
    CREATE USER [CMC_Logiwa_app_dev] FOR LOGIN [CMC_Logiwa_app_dev] WITH DEFAULT_SCHEMA = [dbo];
GO
ALTER ROLE db_datareader ADD MEMBER [CMC_Logiwa_app_dev];
ALTER ROLE db_datawriter ADD MEMBER [CMC_Logiwa_app_dev];
GRANT EXECUTE ON SCHEMA::dbo TO [CMC_Logiwa_app_dev];
GO
PRINT 'CMC_Logiwa_app_dev: read, write, execute on Local_CMC_Logiwa_DEV.';
GO
GO
-- ===== 3. procedures =====
/*
  CMC_Logiwa local database - stored procedures (DEV), part 1
  Covers: login, order sync, ENQ lookup, reset/reject/events, outbox (result push + label requests), archive.
  NOT included yet: sp_Update_ACK_Status, sp_Get_PRN_Data, sp_Update_PRN, sp_Update_LAB, sp_Update_END, sp_Update_REM
  (the CMC machine procs; they are ported from the V1 definitions, see DOC\export-v1-procs.txt).
  Safe to re-run (CREATE OR ALTER).
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ================================================================ LOGIN */
CREATE OR ALTER PROCEDURE dbo.sp_User_Count
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) AS UserCount FROM dbo.Users;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_User_Get
    @Username nvarchar(50)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT UserId, Username, PasswordHash, Salt, Iterations, IsActive
    FROM dbo.Users
    WHERE Username = @Username;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_User_Create
    @Username     nvarchar(50),
    @PasswordHash varbinary(64),
    @Salt         varbinary(32),
    @Iterations   int
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM dbo.Users WHERE Username = @Username)
    BEGIN
        SELECT CAST(0 AS int) AS UserId, N'EXISTS' AS Result;
        RETURN;
    END
    INSERT dbo.Users(Username, PasswordHash, Salt, Iterations) VALUES (@Username, @PasswordHash, @Salt, @Iterations);
    SELECT CAST(SCOPE_IDENTITY() AS int) AS UserId, N'OK' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_User_SetPassword
    @Username     nvarchar(50),
    @PasswordHash varbinary(64),
    @Salt         varbinary(32),
    @Iterations   int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Users SET PasswordHash = @PasswordHash, Salt = @Salt, Iterations = @Iterations WHERE Username = @Username;
    SELECT @@ROWCOUNT AS Updated;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_User_Touch
    @Username nvarchar(50)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Users SET LastLoginAt = SYSDATETIME() WHERE Username = @Username;
END
GO

/* ================================================================ EVENTS */
CREATE OR ALTER PROCEDURE dbo.sp_Event_Add
    @EventType   nvarchar(20),
    @OrderNumber nvarchar(50)  = NULL,
    @PackJobID   int           = NULL,
    @User        nvarchar(50)  = NULL,
    @MachineId   nvarchar(20)  = NULL,
    @Detail      nvarchar(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
    VALUES (@PackJobID, @OrderNumber, @EventType, @User, @MachineId, @Detail);
END
GO

/* ================================================================ SYNC
   @Json = array of { syncSeq, trackingNumber, shipmentIdentifier, orderCode, clientCode, labelZpl }
   (one page from GET /api/cmc/orders). @MaxSeq = maxSeq of that page.            */
CREATE OR ALTER PROCEDURE dbo.sp_Sync_UpsertOrders
    @Json   nvarchar(max),
    @MaxSeq bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @in TABLE(
        TrackingNumber     nvarchar(50)  NOT NULL PRIMARY KEY,
        SyncSeq            bigint        NOT NULL,
        ShipmentIdentifier nvarchar(64)  NULL,
        OrderCode          nvarchar(100) NULL,
        ClientCode         nvarchar(50)  NULL,
        LabelZpl           nvarchar(max) NULL
    );

    -- keep the newest version of a tracking number if it appears twice in a page
    INSERT @in(TrackingNumber, SyncSeq, ShipmentIdentifier, OrderCode, ClientCode, LabelZpl)
    SELECT TrackingNumber, SyncSeq, ShipmentIdentifier, OrderCode, ClientCode, NULLIF(LabelZpl, N'')
    FROM (
        SELECT j.TrackingNumber, j.SyncSeq, j.ShipmentIdentifier, j.OrderCode, j.ClientCode, j.LabelZpl,
               ROW_NUMBER() OVER (PARTITION BY j.TrackingNumber ORDER BY j.SyncSeq DESC) AS rn
        FROM OPENJSON(@Json) WITH (
            SyncSeq            bigint        '$.syncSeq',
            TrackingNumber     nvarchar(50)  '$.trackingNumber',
            ShipmentIdentifier nvarchar(64)  '$.shipmentIdentifier',
            OrderCode          nvarchar(100) '$.orderCode',
            ClientCode         nvarchar(50)  '$.clientCode',
            LabelZpl           nvarchar(max) '$.labelZpl'
        ) AS j
        WHERE NULLIF(LTRIM(RTRIM(j.TrackingNumber)), N'') IS NOT NULL
    ) AS x
    WHERE rn = 1;

    DECLARE @updated int = 0, @inserted int = 0;

    BEGIN TRAN;

    -- existing current rows: refresh identifiers; a label may only be replaced while the carton is not being processed
    UPDATE p SET
        ShipmentIdentifier = COALESCE(i.ShipmentIdentifier, p.ShipmentIdentifier),
        OrderCode          = COALESCE(i.OrderCode, p.OrderCode),
        ClientCode         = COALESCE(i.ClientCode, p.ClientCode),
        SyncSeq            = i.SyncSeq,
        LabelData    = CASE WHEN p.OrderStatus IN (N'NoLabel', N'Ready') AND i.LabelZpl IS NOT NULL THEN i.LabelZpl ELSE p.LabelData END,
        LabelDataLen = CASE WHEN p.OrderStatus IN (N'NoLabel', N'Ready') AND i.LabelZpl IS NOT NULL THEN LEN(i.LabelZpl) ELSE p.LabelDataLen END,
        OrderStatus  = CASE WHEN p.OrderStatus = N'NoLabel' AND i.LabelZpl IS NOT NULL THEN N'Ready' ELSE p.OrderStatus END
    FROM dbo.PackJob p
    JOIN @in i ON i.TrackingNumber = p.OrderNumber
    WHERE p.IsCurrent = 1;
    SET @updated = @@ROWCOUNT;

    -- new orders (never seen locally, not archived)
    INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                       LabelData, LabelDataLen, OrderStatus, SyncSeq, IsCurrent)
    SELECT i.TrackingNumber, i.TrackingNumber, i.TrackingNumber, i.ShipmentIdentifier, i.OrderCode, i.ClientCode,
           i.LabelZpl, LEN(i.LabelZpl),
           CASE WHEN i.LabelZpl IS NULL THEN N'NoLabel' ELSE N'Ready' END,
           i.SyncSeq, 1
    FROM @in i
    WHERE NOT EXISTS (SELECT 1 FROM dbo.PackJob p WHERE p.OrderNumber = i.TrackingNumber)
      AND NOT EXISTS (SELECT 1 FROM dbo.PackJobArchive a WHERE a.OrderNumber = i.TrackingNumber);
    SET @inserted = @@ROWCOUNT;

    -- label arrived: close open label requests
    UPDATE r SET Status = N'Done'
    FROM dbo.LabelRequest r
    JOIN dbo.PackJob p ON p.OrderNumber = r.OrderNumber AND p.IsCurrent = 1
    WHERE r.Status IN (N'Pending', N'Sent') AND p.OrderStatus <> N'NoLabel';

    UPDATE dbo.SyncState
    SET LastSyncSeq = CASE WHEN @MaxSeq > LastSyncSeq THEN @MaxSeq ELSE LastSyncSeq END,
        LastPollAt = SYSDATETIME(), LastPollError = NULL
    WHERE Id = 1;

    COMMIT;

    SELECT @inserted AS Inserted, @updated AS Updated;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_Sync_GetState
AS
BEGIN
    SET NOCOUNT ON;
    SELECT LastSyncSeq, LastPollAt, LastPollError FROM dbo.SyncState WHERE Id = 1;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_Sync_SetError
    @Error nvarchar(400)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.SyncState SET LastPollAt = SYSDATETIME(), LastPollError = @Error WHERE Id = 1;
END
GO

/* ================================================================ ENQ
   Returns one row: EnqResult (OK / NOT_FOUND / DUPLICATE / NO_LABEL), PackJobID, MatchLab, Description.
   Claiming the row is one atomic UPDATE, so two simultaneous scans cannot both get OK.                  */
CREATE OR ALTER PROCEDURE dbo.sp_ENQ_Get_Data
    @Barcode   nvarchar(100),
    @Counter   nvarchar(20) = NULL,
    @MachineId nvarchar(20) = NULL,
    @User      nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @bc nvarchar(50) = LEFT(LTRIM(RTRIM(ISNULL(@Barcode, N''))), 50);
    DECLARE @claimed TABLE(PackJobID int, MatchLab nvarchar(100));

    UPDATE TOP (1) dbo.PackJob
    SET OrderStatus = N'InProgress', ProcessStartedDateTime = SYSDATETIME(),
        PackID = LEFT(@Counter, 20), MachineId = @MachineId, ProcessedByUser = @User
    OUTPUT inserted.PackJobID, inserted.MatchLab INTO @claimed
    WHERE OrderNumber = @bc AND IsCurrent = 1 AND OrderStatus = N'Ready';

    IF EXISTS (SELECT 1 FROM @claimed)
    BEGIN
        DECLARE @okId int = (SELECT TOP 1 PackJobID FROM @claimed);
        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
        VALUES (@okId, @bc, N'Scan', @User, @MachineId, N'ENQ ok, counter ' + ISNULL(@Counter, N''));
        SELECT N'OK' AS EnqResult, PackJobID, MatchLab, @bc AS Description FROM @claimed;
        RETURN;
    END

    DECLARE @id int, @status nvarchar(20);
    SELECT TOP 1 @id = PackJobID, @status = OrderStatus
    FROM dbo.PackJob WHERE OrderNumber = @bc AND IsCurrent = 1;

    IF @id IS NOT NULL AND @status = N'NoLabel'
    BEGIN
        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
        VALUES (@id, @bc, N'NoLabel', @User, @MachineId, N'Scan without label');

        -- one open request per tracking number (also not again within 2 minutes of the last one sent)
        IF NOT EXISTS (SELECT 1 FROM dbo.LabelRequest
                       WHERE OrderNumber = @bc
                         AND (Status = N'Pending' OR (Status = N'Sent' AND SentAt > DATEADD(MINUTE, -2, SYSDATETIME()))))
            INSERT dbo.LabelRequest(OrderNumber, MachineId) VALUES (@bc, @MachineId);

        SELECT N'NO_LABEL' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
               N'No label, try again later' AS Description;
        RETURN;
    END

    IF @id IS NOT NULL OR EXISTS (SELECT 1 FROM dbo.PackJobArchive WHERE OrderNumber = @bc)
    BEGIN
        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
        VALUES (@id, @bc, N'Duplicate', @User, @MachineId,
                CASE WHEN @id IS NULL THEN N'Already processed (archived)' ELSE N'Already processed, status ' + @status END);
        SELECT N'DUPLICATE' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
               N'Duplicate' AS Description;
        RETURN;
    END

    INSERT dbo.PackJobEvent(OrderNumber, EventType, UserName, MachineId, Detail)
    VALUES (@bc, N'NotFound', @User, @MachineId, N'Scan not found');
    SELECT N'NOT_FOUND' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
           N'Not found, try again later' AS Description;
END
GO

/* ================================================================ RESET / REJECT
   Reset: old current row becomes history (IsCurrent = 0), a new Ready row is created from it (or from the archive). */
CREATE OR ALTER PROCEDURE dbo.sp_Reset_ByOrder
    @OrderNumber nvarchar(50),
    @User        nvarchar(50)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @o nvarchar(50) = LEFT(LTRIM(RTRIM(ISNULL(@OrderNumber, N''))), 50);
    DECLARE @src int, @status nvarchar(20), @fromArchive bit = 0;

    SELECT TOP 1 @src = PackJobID, @status = OrderStatus FROM dbo.PackJob WHERE OrderNumber = @o AND IsCurrent = 1;

    IF @src IS NULL
    BEGIN
        SELECT TOP 1 @src = PackJobID, @status = OrderStatus FROM dbo.PackJobArchive WHERE OrderNumber = @o ORDER BY PackJobID DESC;
        SET @fromArchive = 1;
    END

    IF @src IS NULL
    BEGIN
        SELECT N'NOT_FOUND' AS Result, CAST(NULL AS int) AS NewPackJobID;
        RETURN;
    END

    IF @fromArchive = 0 AND @status IN (N'Ready', N'NoLabel')
    BEGIN
        SELECT N'NOTHING_TO_RESET' AS Result, @src AS NewPackJobID;
        RETURN;
    END

    DECLARE @new TABLE(PackJobID int);

    BEGIN TRAN;

    IF @fromArchive = 0
        UPDATE dbo.PackJob SET IsCurrent = 0 WHERE PackJobID = @src;

    IF @fromArchive = 0
        INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                           LabelData, LabelDataLen, OrderStatus, SyncSeq, IsCurrent,
                           ResetFromPackJobID, ResetByUser, ResetAt)
        OUTPUT inserted.PackJobID INTO @new
        SELECT JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
               LabelData, LabelDataLen,
               CASE WHEN NULLIF(LabelData, N'') IS NULL THEN N'NoLabel' ELSE N'Ready' END,
               SyncSeq, 1, PackJobID, @User, SYSDATETIME()
        FROM dbo.PackJob WHERE PackJobID = @src;
    ELSE
        INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                           LabelData, LabelDataLen, OrderStatus, SyncSeq, IsCurrent,
                           ResetFromPackJobID, ResetByUser, ResetAt)
        OUTPUT inserted.PackJobID INTO @new
        SELECT JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
               LabelData, LabelDataLen,
               CASE WHEN NULLIF(LabelData, N'') IS NULL THEN N'NoLabel' ELSE N'Ready' END,
               SyncSeq, 1, PackJobID, @User, SYSDATETIME()
        FROM dbo.PackJobArchive WHERE PackJobID = @src;

    DECLARE @newId int = (SELECT TOP 1 PackJobID FROM @new);

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, Detail)
    VALUES (@newId, @o, N'Reset', @User,
            N'Reset from PackJobID ' + CAST(@src AS nvarchar(20)) + CASE WHEN @fromArchive = 1 THEN N' (archive)' ELSE N'' END);

    COMMIT;

    SELECT N'OK' AS Result, @newId AS NewPackJobID;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_Reject_ByOrder
    @OrderNumber nvarchar(50),
    @User        nvarchar(50)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, Detail)
    SELECT (SELECT TOP 1 PackJobID FROM dbo.PackJob WHERE OrderNumber = @OrderNumber AND IsCurrent = 1),
           @OrderNumber, N'Reject', @User, N'Duplicate rejected by operator';
END
GO

/* Duplicate scans from the last @Hours hours that nobody has reset or rejected yet (for the WPF screen). */
CREATE OR ALTER PROCEDURE dbo.sp_List_RecentDuplicates
    @Hours int = 24
AS
BEGIN
    SET NOCOUNT ON;
    SELECT e.OrderNumber,
           MAX(e.EventAt) AS LastScan,
           COUNT(*)       AS Scans,
           MAX(p.PackJobID)   AS PackJobID,
           MAX(p.OrderStatus) AS OrderStatus,
           MAX(p.OrderCode)   AS OrderCode
    FROM dbo.PackJobEvent e
    LEFT JOIN dbo.PackJob p ON p.OrderNumber = e.OrderNumber AND p.IsCurrent = 1
    WHERE e.EventType = N'Duplicate'
      AND e.EventAt >= DATEADD(HOUR, -@Hours, SYSDATETIME())
      AND NOT EXISTS (SELECT 1 FROM dbo.PackJobEvent r
                      WHERE r.OrderNumber = e.OrderNumber AND r.EventType IN (N'Reset', N'Reject') AND r.EventAt > e.EventAt)
    GROUP BY e.OrderNumber
    ORDER BY MAX(e.EventAt) DESC;
END
GO

/* ================================================================ OUTBOX: results to the web VM */
CREATE OR ALTER PROCEDURE dbo.sp_Outbox_GetNext
    @Max int = 20
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) PackJobID, OrderNumber AS TrackingNumber, MachineId, ProcessCompletedDateTime AS CompletedAt,
           WeightScale AS BoxWeightG, BoxSizeL AS LengthMm, BoxSizeW AS WidthMm, BoxSizeH AS HeightMm
    FROM dbo.PackJob
    WHERE PushStatus = N'Pending' AND (PushNextAt IS NULL OR PushNextAt <= SYSDATETIME())
    ORDER BY PackJobID;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_Outbox_MarkSent
    @PackJobID int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.PackJob SET PushStatus = N'Sent', PushedAt = SYSDATETIME(), PushLastError = NULL, PushNextAt = NULL
    WHERE PackJobID = @PackJobID;
END
GO

/* Retry forever with backoff 10 s, 20, 40, 80, 160, then every 300 s. */
CREATE OR ALTER PROCEDURE dbo.sp_Outbox_MarkFailed
    @PackJobID int,
    @Error     nvarchar(400)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.PackJob
    SET PushLastError = LEFT(@Error, 400),
        PushNextAt    = DATEADD(SECOND, CASE WHEN PushAttempts >= 5 THEN 300 ELSE 10 * POWER(2, PushAttempts) END, SYSDATETIME()),
        PushAttempts  = PushAttempts + 1
    WHERE PackJobID = @PackJobID;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_Outbox_Counts
AS
BEGIN
    SET NOCOUNT ON;
    SELECT (SELECT COUNT(*) FROM dbo.PackJob WHERE PushStatus = N'Pending') AS PendingResults,
           (SELECT COUNT(*) FROM dbo.LabelRequest WHERE Status = N'Pending') AS PendingLabelRequests;
END
GO

/* ================================================================ OUTBOX: label requests */
CREATE OR ALTER PROCEDURE dbo.sp_LabelReq_GetNext
    @Max int = 20
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) RequestId, OrderNumber AS TrackingNumber, MachineId, RequestedAt
    FROM dbo.LabelRequest
    WHERE Status = N'Pending' AND (NextAttemptAt IS NULL OR NextAttemptAt <= SYSDATETIME())
    ORDER BY RequestId;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_LabelReq_MarkSent
    @RequestId int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.LabelRequest SET Status = N'Sent', SentAt = SYSDATETIME(), LastError = NULL, NextAttemptAt = NULL
    WHERE RequestId = @RequestId AND Status = N'Pending';
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_LabelReq_MarkFailed
    @RequestId int,
    @Error     nvarchar(400)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.LabelRequest
    SET LastError     = LEFT(@Error, 400),
        NextAttemptAt = DATEADD(SECOND, CASE WHEN Attempts >= 5 THEN 300 ELSE 10 * POWER(2, Attempts) END, SYSDATETIME()),
        Attempts      = Attempts + 1
    WHERE RequestId = @RequestId;
END
GO

/* ================================================================ ARCHIVE (move, never delete)
   Rows go to PackJobArchive after @Days days when they are finished and nothing is waiting to be pushed.
   Events older than the cutoff go to PackJobEventArchive. Runs in small batches.                       */
CREATE OR ALTER PROCEDURE dbo.sp_Archive_Run
    @Days  int = 30,
    @Batch int = 500
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @cutoff datetime2(0) = DATEADD(DAY, -@Days, SYSDATETIME());
    DECLARE @n int = 1, @jobs int = 0, @events int = 0;

    WHILE @n > 0
    BEGIN
        BEGIN TRAN;
        DELETE TOP (@Batch) FROM dbo.PackJob
        OUTPUT deleted.PackJobID, deleted.JobCreatedDateTime, deleted.JobNumber, deleted.OrderNumber, deleted.MatchLab,
               deleted.ShipmentIdentifier, deleted.OrderCode, deleted.ClientCode, deleted.LabelData, deleted.LabelDataLen,
               deleted.OrderStatus, deleted.ProcessStartedDateTime, deleted.ProcessCompletedDateTime, deleted.PackID,
               deleted.BoxEventID, deleted.BoxEventText, deleted.FlagBoxLow, deleted.HasBoxIssue, deleted.BoxIssueComment,
               deleted.BoxIssueDateTime, deleted.BoxSizeH, deleted.BoxSizeL, deleted.BoxSizeW, deleted.BoxArea,
               deleted.WeightCarton, deleted.WeightScale, deleted.MachineId, deleted.ProcessedByUser, deleted.SyncSeq,
               deleted.IsCurrent, deleted.ResetFromPackJobID, deleted.ResetByUser, deleted.ResetAt, deleted.PushStatus,
               deleted.PushAttempts, deleted.PushNextAt, deleted.PushLastError, deleted.PushedAt, SYSDATETIME()
        INTO dbo.PackJobArchive(PackJobID, JobCreatedDateTime, JobNumber, OrderNumber, MatchLab,
               ShipmentIdentifier, OrderCode, ClientCode, LabelData, LabelDataLen,
               OrderStatus, ProcessStartedDateTime, ProcessCompletedDateTime, PackID,
               BoxEventID, BoxEventText, FlagBoxLow, HasBoxIssue, BoxIssueComment,
               BoxIssueDateTime, BoxSizeH, BoxSizeL, BoxSizeW, BoxArea,
               WeightCarton, WeightScale, MachineId, ProcessedByUser, SyncSeq,
               IsCurrent, ResetFromPackJobID, ResetByUser, ResetAt, PushStatus,
               PushAttempts, PushNextAt, PushLastError, PushedAt, ArchivedAt)
        WHERE PushStatus IN (N'None', N'Sent')
          AND (IsCurrent = 0 OR OrderStatus IN (N'Completed', N'Failed', N'Removed'))
          AND COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime, JobCreatedDateTime) < @cutoff;
        SET @n = @@ROWCOUNT;
        SET @jobs += @n;
        COMMIT;
    END

    SET @n = 1;
    WHILE @n > 0
    BEGIN
        BEGIN TRAN;
        DELETE TOP (@Batch) FROM dbo.PackJobEvent
        OUTPUT deleted.EventId, deleted.PackJobID, deleted.OrderNumber, deleted.EventType, deleted.EventAt,
               deleted.UserName, deleted.MachineId, deleted.Detail
        INTO dbo.PackJobEventArchive(EventId, PackJobID, OrderNumber, EventType, EventAt, UserName, MachineId, Detail)
        WHERE EventAt < @cutoff;
        SET @n = @@ROWCOUNT;
        SET @events += @n;
        COMMIT;
    END

    SELECT @jobs AS JobsArchived, @events AS EventsArchived;
END
GO
GO
-- ===== 4. machine procedures =====
/*
  CMC_Logiwa local database - machine procedures (DEV)
  Ported from the V1 definitions (DOC\v1-procs.sql) with the same rules, adapted to the new table:
    - V1 statuses ENQ/ACK/PRN/END/Failed ACK/Failed PRN/Failed End/REMOVED become the fixed set
      InProgress / Completed / Failed / Removed; the step detail goes to PackJobEvent.
    - sp_Update_END also queues the result for the web VM (PushStatus = Pending) on a successful END.
    - Nothing here talks to Logiwa.
  Safe to re-run (CREATE OR ALTER). Run after 001 and 002.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ACK from the machine: '1' ok (nothing to change), '0' failed. */
CREATE OR ALTER PROCEDURE dbo.sp_Update_ACK_Status
    @PackID     NVARCHAR(20),
    @PackJobID  INT,
    @ACK_Status NVARCHAR(1)
AS
BEGIN
    SET NOCOUNT ON;

    IF @ACK_Status = N'0'
        UPDATE dbo.PackJob SET OrderStatus = N'Failed' WHERE PackJobID = @PackJobID;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'ACK', MachineId,
           CASE WHEN @ACK_Status = N'1' THEN N'ACK ok'
                WHEN @ACK_Status = N'0' THEN N'ACK failed'
                ELSE N'ACK status ' + ISNULL(@ACK_Status, N'?') END
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    SELECT OrderNumber FROM dbo.PackJob WHERE PackJobID = @PackJobID;
END
GO

/* Label to print. No row is returned (so no PRN is sent) when the label is missing. */
CREATE OR ALTER PROCEDURE dbo.sp_Get_PRN_Data
    @PackJobID INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @LabelData NVARCHAR(MAX);

    SELECT @LabelData = LabelData FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    IF @LabelData IS NULL OR LTRIM(RTRIM(@LabelData)) = N''
    BEGIN
        UPDATE dbo.PackJob
        SET HasBoxIssue = 1,
            BoxIssueComment = N'LabelData missing - PRN not sent',
            BoxIssueDateTime = SYSDATETIME()
        WHERE PackJobID = @PackJobID;

        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
        SELECT PackJobID, OrderNumber, N'PRN', MachineId, N'Label missing, PRN not sent'
        FROM dbo.PackJob WHERE PackJobID = @PackJobID;

        -- one row with an empty label (the app sends no PRN and shows the reason on the front panel)
        SELECT CAST(NULL AS NVARCHAR(20)) AS PackID, CAST(NULL AS NVARCHAR(MAX)) AS LabelData, OrderNumber
        FROM dbo.PackJob WHERE PackJobID = @PackJobID;
        RETURN;
    END

    SELECT PackID, LabelData, OrderNumber FROM dbo.PackJob WHERE PackJobID = @PackJobID;
END
GO

/* prn result from the machine: '1' printed, '0' failed. */
CREATE OR ALTER PROCEDURE dbo.sp_Update_PRN
    @PackJobID INT,
    @PRNResult NVARCHAR(1)
AS
BEGIN
    SET NOCOUNT ON;

    IF @PRNResult = N'0'
        UPDATE dbo.PackJob SET OrderStatus = N'Failed' WHERE PackJobID = @PackJobID;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'PRN', MachineId,
           CASE WHEN @PRNResult = N'1' THEN N'Print ok'
                WHEN @PRNResult = N'0' THEN N'Print failed'
                ELSE N'Print result ' + ISNULL(@PRNResult, N'?') END
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    SELECT OrderNumber FROM dbo.PackJob WHERE PackJobID = @PackJobID;
END
GO

/* LAB (label check and measurement). Returns LABResults (1 = ok) and MatchLab, as V1 did. */
CREATE OR ALTER PROCEDURE dbo.sp_Update_LAB
    @PackJobID       NVARCHAR(20),
    @LabStatus       INT,
    @LabEvent        NVARCHAR(20),
    @LabSizeH        INT,
    @LabSizeL        INT,
    @LabSizeW        INT,
    @LabArea         INT,
    @LabWeightCarton INT,
    @LabWeightScale  INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @id INT = TRY_CONVERT(INT, @PackJobID);
    DECLARE @EventText NVARCHAR(100), @LABResults INT, @HasBoxIssue BIT, @MatchLab NVARCHAR(100), @found INT = 0,
            @OrderNumber NVARCHAR(50);

    SELECT TOP (1) @EventText = EventText FROM dbo.Events WHERE EventCode = @LabEvent;
    SET @EventText = ISNULL(@EventText, N'Unknown Event');

    SELECT @HasBoxIssue = HasBoxIssue, @MatchLab = MatchLab, @OrderNumber = OrderNumber, @found = 1
    FROM dbo.PackJob WHERE PackJobID = @id;

    IF @found = 0
    BEGIN
        SELECT CAST(0 AS INT) AS LABResults, CAST(NULL AS NVARCHAR(100)) AS MatchLab;   -- unknown carton: reject
        RETURN;
    END

    IF @HasBoxIssue = 1
        SET @LABResults = 0;
    ELSE
        SET @LABResults = CASE WHEN @LabStatus = 1 THEN 1 ELSE 0 END;

    UPDATE dbo.PackJob
    SET OrderStatus  = CASE WHEN @LABResults = 1 THEN OrderStatus ELSE N'Failed' END,
        BoxEventID   = @LabEvent,
        BoxEventText = @EventText,
        BoxSizeH = @LabSizeH,
        BoxSizeL = @LabSizeL,
        BoxSizeW = @LabSizeW,
        BoxArea  = @LabArea,
        WeightCarton = @LabWeightCarton,
        WeightScale  = @LabWeightScale
    WHERE PackJobID = @id;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'LAB', MachineId,
           LEFT((CASE WHEN @LABResults = 1 THEN N'Label ok' ELSE N'Label failed' END) + N': ' + @EventText, 400)
    FROM dbo.PackJob WHERE PackJobID = @id;

    SELECT @LABResults AS LABResults, @MatchLab AS MatchLab,
           @OrderNumber AS OrderNumber, @LabEvent AS EventCode, @EventText AS EventText;
END
GO

/* END (carton finished). Stores the measured size/weight. A successful END is queued for the web VM
   (PushStatus = Pending, once). Returns FinalResult (1 = success).                                   */
CREATE OR ALTER PROCEDURE dbo.sp_Update_END
    @PackJobID       INT,
    @EndStatus       INT,
    @EndEvent        NVARCHAR(20),
    @EndSizeH        INT,
    @EndSizeL        INT,
    @EndSizeW        INT,
    @EndArea         INT,
    @EndWeightCarton INT,
    @EndWeightScale  INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EventText NVARCHAR(100), @HasBoxIssue BIT, @FinalResult INT, @push NVARCHAR(10), @found INT = 0;

    DECLARE @OrderNumber NVARCHAR(50);

    SELECT @HasBoxIssue = ISNULL(HasBoxIssue, 0), @push = PushStatus, @OrderNumber = OrderNumber, @found = 1
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    IF @found = 0
    BEGIN
        SELECT CAST(0 AS INT) AS FinalResult;
        RETURN;
    END

    -- the result was already sent to the web VM: a repeated END must not change it
    IF @push = N'Sent'
    BEGIN
        SELECT CAST(1 AS INT) AS FinalResult;
        RETURN;
    END

    SELECT TOP (1) @EventText = EventText FROM dbo.Events WHERE EventCode = @EndEvent;
    SET @EventText = ISNULL(@EventText, N'Unknown Event');

    IF @HasBoxIssue = 1
        SET @FinalResult = 0;
    ELSE
        SET @FinalResult = CASE WHEN @EndStatus = 1 THEN 1 ELSE 0 END;

    UPDATE dbo.PackJob
    SET OrderStatus  = CASE WHEN @FinalResult = 1 THEN N'Completed' ELSE N'Failed' END,
        BoxEventID   = @EndEvent,
        BoxEventText = @EventText,
        BoxSizeH = @EndSizeH,
        BoxSizeL = @EndSizeL,
        BoxSizeW = @EndSizeW,
        BoxArea  = @EndArea,
        WeightCarton = @EndWeightCarton,
        WeightScale  = @EndWeightScale,
        ProcessCompletedDateTime = SYSDATETIME(),
        PushStatus   = CASE WHEN @FinalResult = 1 THEN N'Pending' ELSE PushStatus END,
        PushAttempts = CASE WHEN @FinalResult = 1 THEN 0 ELSE PushAttempts END,
        PushNextAt   = NULL,
        PushLastError = NULL
    WHERE PackJobID = @PackJobID;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'END', MachineId,
           LEFT((CASE WHEN @FinalResult = 1 THEN N'End ok' ELSE N'End failed' END) + N': ' + @EventText, 400)
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    SELECT @FinalResult AS FinalResult, @OrderNumber AS OrderNumber, @EndEvent AS EventCode, @EventText AS EventText;
END
GO

/* REM (carton removed from the machine, for any reason incl. a forced reject at the machine). Local only.
   The REM message carries no reason, so the record gets machine event 010 "Box Removed" (text from dbo.Events).
   Returns the tracking number so the front panel can name the carton.                                       */
CREATE OR ALTER PROCEDURE dbo.sp_Update_REM
    @PackJobID INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EventText NVARCHAR(100);
    SELECT TOP (1) @EventText = EventText FROM dbo.Events WHERE EventCode = N'010';
    SET @EventText = ISNULL(@EventText, N'Box Removed');

    UPDATE dbo.PackJob
    SET OrderStatus = N'Removed',
        BoxEventID = N'010',
        BoxEventText = @EventText,
        ProcessCompletedDateTime = SYSDATETIME()
    WHERE PackJobID = @PackJobID;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'REM', MachineId, LEFT(N'Carton removed: ' + @EventText + N' (010)', 400)
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    SELECT OrderNumber FROM dbo.PackJob WHERE PackJobID = @PackJobID;
END
GO
GO
-- ===== 5. machine event codes =====
USE [Local_CMC_Logiwa_DEV];
GO
DELETE FROM dbo.Events;
INSERT dbo.Events(EventCode, EventText) VALUES (N'000', N'GOOD PRODUCT');
INSERT dbo.Events(EventCode, EventText) VALUES (N'001', N'Main Barcode not present');
INSERT dbo.Events(EventCode, EventText) VALUES (N'002', N'Wrong ID from machine');
INSERT dbo.Events(EventCode, EventText) VALUES (N'003', N'Wrong Size H');
INSERT dbo.Events(EventCode, EventText) VALUES (N'004', N'Wrong Size L');
INSERT dbo.Events(EventCode, EventText) VALUES (N'005', N'Wrong Size W');
INSERT dbo.Events(EventCode, EventText) VALUES (N'006', N'Wrong Area (too big)');
INSERT dbo.Events(EventCode, EventText) VALUES (N'007', N'NoRead BClabel');
INSERT dbo.Events(EventCode, EventText) VALUES (N'008', N'BOXbarcode not present');
INSERT dbo.Events(EventCode, EventText) VALUES (N'009', N'Barcode or Reference not assigned');
INSERT dbo.Events(EventCode, EventText) VALUES (N'010', N'Box Removed');
INSERT dbo.Events(EventCode, EventText) VALUES (N'011', N'Error Matching BCLabel');
INSERT dbo.Events(EventCode, EventText) VALUES (N'012', N'Wrong Size on InFeed');
INSERT dbo.Events(EventCode, EventText) VALUES (N'013', N'Not received confirmation on InFeed');
INSERT dbo.Events(EventCode, EventText) VALUES (N'014', N'Label not sent correctly from CIS');
INSERT dbo.Events(EventCode, EventText) VALUES (N'015', N'Invalid Invoice parameter on Incoming item');
INSERT dbo.Events(EventCode, EventText) VALUES (N'016', N'NoRead MTD4 reader');
INSERT dbo.Events(EventCode, EventText) VALUES (N'017', N'Error Matching MTD4 reader');
INSERT dbo.Events(EventCode, EventText) VALUES (N'018', N'NoData from MTD4 reader');
INSERT dbo.Events(EventCode, EventText) VALUES (N'019', N'Forced Reject from customer (LAB message)');
INSERT dbo.Events(EventCode, EventText) VALUES (N'020', N'No answer on INV Message');
INSERT dbo.Events(EventCode, EventText) VALUES (N'021', N'No answer on LAB message');
INSERT dbo.Events(EventCode, EventText) VALUES (N'022', N'Invoice not sent correctly from CIS');
INSERT dbo.Events(EventCode, EventText) VALUES (N'023', N'Not classified');
INSERT dbo.Events(EventCode, EventText) VALUES (N'024', N'Not classified');
INSERT dbo.Events(EventCode, EventText) VALUES (N'025', N'Not classified');
INSERT dbo.Events(EventCode, EventText) VALUES (N'100', N'Not classified');
INSERT dbo.Events(EventCode, EventText) VALUES (N'101', N'Product out of box');
INSERT dbo.Events(EventCode, EventText) VALUES (N'102', N'Flap anomaly');
INSERT dbo.Events(EventCode, EventText) VALUES (N'103', N'Inserting anomaly');
INSERT dbo.Events(EventCode, EventText) VALUES (N'104', N'Error flap on closing');
INSERT dbo.Events(EventCode, EventText) VALUES (N'105', N'Error flag not coded');
INSERT dbo.Events(EventCode, EventText) VALUES (N'106', N'Not classified');
GO
-- ===== 6. test cartons =====
/* Test cartons for the first CMC machine test (ClientCode TEST). Remove later with cleanup-test-cartons.sql. */
USE [Local_CMC_Logiwa_DEV];
GO
IF NOT EXISTS (SELECT 1 FROM dbo.PackJob WHERE OrderNumber = N'1JDQ12002728')
  INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode, LabelData, LabelDataLen, OrderStatus, IsCurrent)
  VALUES (N'1JDQ12002728', N'1JDQ12002728', N'1JDQ12002728', N'TEST-SHIPMENT-1', N'TEST-CARTON-1', N'TEST', N'^XA^LL1800^PW1200^FO0,0^GB1200,178,178^FS^A0N,148,141^FO44,30^FR^FDParcel Post^FS^A0N,43,43^FO872,21^FR^FDAUSTRALIA^FS^A0N,103,133^FO865,67^FR^FDPOST^FS^A0N,67,59^FO22,229^FDDELIVER TO^FS^A0N,59,56^FO22,325^FDSuper market^FS^A0N,59,56^FO22,399^FDSuper market^FS^A0N,59,56^FO22,473^FD85 Grose Street^FS^A0N,59,56^FO22,547^FD85 Grose Street 2^FS^A0N,59,56^FO22,621^FDParramatta NSW 2150^FS^A0N,44,44^FO353,229^FDPHONE: 0430813024^FS^FO918,222^BXN,9,200,,,5,,1^FH_^FD0199312650999998911JDQ1200272801000930308_1D4202150_1D8008260416113153^FS^FO0,783^GB1200,4,4^FS^A0N,44,40^FO22,805^FDDELIVERY INSTRUCTIONS^FS^A0N,44,40^FO22,850^FDexample^FS^A0N,44,40^FO22,894^FD^FS^A0N,44,40^FO22,939^FD^FS^A0N,59,59^FO936,792^FD2.00kg^FS^FO900,852^BY3,2,1^BCN,70,N,N,N,N^FD3661^FS^A0N,59,56^FO936,932^FD3661^FS^FO0,989^GB1200,4,4^FS^FO681,989^GB4,118,4^FS^FO0,1100^GB1200,4,4^FS^A0N,44,40^FO22,1012^FDSIGNATURE ON DELIVERY REQUIRED^FS^A0N,44,40^FO700,1012^FDCON NO 1JDQ12002728^FS^A0N,44,40^FO700,1059^FDPARCEL 1 OF 1^FS^A0N,37,37^FO266,1123^FDAP Article ID: 1JDQ12002728010009303^FS^FO165,1192^BY3,2,1^BCN,230,N,N,N,N^FD1JDQ12002728010009303^FS^A0N,37,37^FO266,1470^FDAP Article ID: 1JDQ12002728010009303^FS^FO0,1506^GB1200,4,4^FS^FO603,1506^GB4,281,4^FS^A0N,30,27^FO22,1524^FDSENDER^FS^A0N,30,27^FO22,1561^FDAutomated Tenant 1^FS^A0N,30,27^FO22,1598^FD85 Grose Street^FS^A0N,30,27^FO22,1636^FDHCM^FS^A0N,30,27^FO22,1673^FDParramatta NSW 2150^FS^A0N,30,27^FO22,1710^FDREF: #3661-20250320260414201107^FS^A0N,30,24^FO622,1524^FDAviation Security and Dangerous Goods Declaration:^FS^A0N,30,24^FO622,1554^FDThe sender acknowledges that this article may be^FS^A0N,30,24^FO622,1583^FDcarried by air and will be subject to aviation security^FS^A0N,30,24^FO622,1613^FDand clearing procedures; and the sender declares that^FS^A0N,30,24^FO622,1642^FDthe article does not contain any dangerous or^FS^A0N,30,24^FO622,1672^FDprohibited goods, explosive or incendiary devices.^FS^A0N,30,24^FO622,1701^FDA false declaration is a criminal offence.^FS^FO0,1742^GB1200,59,59^FS^A0N,37,37^FO413,1758^FR^FDGenerated by 3PLWMS^FS^XZ', 2094, N'Ready', 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PackJob WHERE OrderNumber = N'1JDQ12002727')
  INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode, LabelData, LabelDataLen, OrderStatus, IsCurrent)
  VALUES (N'1JDQ12002727', N'1JDQ12002727', N'1JDQ12002727', N'TEST-SHIPMENT-2', N'TEST-CARTON-2', N'TEST', N'^XA^LL1800^PW1200^FO0,0^GB1200,178,178^FS^A0N,148,141^FO44,30^FR^FDParcel Post^FS^A0N,43,43^FO872,21^FR^FDAUSTRALIA^FS^A0N,103,133^FO865,67^FR^FDPOST^FS^A0N,67,59^FO22,229^FDDELIVER TO^FS^A0N,59,56^FO22,325^FDSuper market^FS^A0N,59,56^FO22,399^FDSuper market^FS^A0N,59,56^FO22,473^FD85 Grose Street^FS^A0N,59,56^FO22,547^FD85 Grose Street 2^FS^A0N,59,56^FO22,621^FDParramatta NSW 2150^FS^A0N,44,44^FO353,229^FDPHONE: 0430813024^FS^FO918,222^BXN,9,200,,,5,,1^FH_^FD0199312650999998911JDQ1200272701000930309_1D4202150_1D8008260416113152^FS^FO0,783^GB1200,4,4^FS^A0N,44,40^FO22,805^FDDELIVERY INSTRUCTIONS^FS^A0N,44,40^FO22,850^FDexample^FS^A0N,44,40^FO22,894^FD^FS^A0N,44,40^FO22,939^FD^FS^A0N,59,59^FO936,792^FD2.00kg^FS^FO900,852^BY3,2,1^BCN,70,N,N,N,N^FD3660^FS^A0N,59,56^FO936,932^FD3660^FS^FO0,989^GB1200,4,4^FS^FO681,989^GB4,118,4^FS^FO0,1100^GB1200,4,4^FS^A0N,44,40^FO22,1012^FDSIGNATURE ON DELIVERY REQUIRED^FS^A0N,44,40^FO700,1012^FDCON NO 1JDQ12002727^FS^A0N,44,40^FO700,1059^FDPARCEL 1 OF 1^FS^A0N,37,37^FO266,1123^FDAP Article ID: 1JDQ12002727010009303^FS^FO165,1192^BY3,2,1^BCN,230,N,N,N,N^FD1JDQ12002727010009303^FS^A0N,37,37^FO266,1470^FDAP Article ID: 1JDQ12002727010009303^FS^FO0,1506^GB1200,4,4^FS^FO603,1506^GB4,281,4^FS^A0N,30,27^FO22,1524^FDSENDER^FS^A0N,30,27^FO22,1561^FDAutomated Tenant 1^FS^A0N,30,27^FO22,1598^FD85 Grose Street^FS^A0N,30,27^FO22,1636^FDHCM^FS^A0N,30,27^FO22,1673^FDParramatta NSW 2150^FS^A0N,30,27^FO22,1710^FDREF: #3660-20250320260414201106^FS^A0N,30,24^FO622,1524^FDAviation Security and Dangerous Goods Declaration:^FS^A0N,30,24^FO622,1554^FDThe sender acknowledges that this article may be^FS^A0N,30,24^FO622,1583^FDcarried by air and will be subject to aviation security^FS^A0N,30,24^FO622,1613^FDand clearing procedures; and the sender declares that^FS^A0N,30,24^FO622,1642^FDthe article does not contain any dangerous or^FS^A0N,30,24^FO622,1672^FDprohibited goods, explosive or incendiary devices.^FS^A0N,30,24^FO622,1701^FDA false declaration is a criminal offence.^FS^FO0,1742^GB1200,59,59^FS^A0N,37,37^FO413,1758^FR^FDGenerated by 3PLWMS^FS^XZ', 2094, N'Ready', 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PackJob WHERE OrderNumber = N'TESTNOLABEL1')
  INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode, LabelData, LabelDataLen, OrderStatus, IsCurrent)
  VALUES (N'TESTNOLABEL1', N'TESTNOLABEL1', N'TESTNOLABEL1', N'TEST-SHIPMENT-3', N'TEST-NOLABEL', N'TEST', NULL, NULL, N'NoLabel', 1);
GO
SELECT PackJobID, OrderNumber, OrderStatus, LabelDataLen FROM dbo.PackJob ORDER BY PackJobID;
GO

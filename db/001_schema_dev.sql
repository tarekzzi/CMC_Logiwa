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

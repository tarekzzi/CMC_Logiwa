/*
  CMC_Logiwa - date fields: local time, UTC and the time Logiwa recorded (DEV)
  The same box showed 26 Nov on the packing PC (local, UTC+11) and 25 Nov on the web VM (UTC). Each box now carries three times, shown side by side:
    JobCreatedDateTime  local time the packing PC stored the box (unchanged)
    JobCreatedUtc       the same moment in UTC (new; set on insert; existing rows are filled in below)
    LogiwaCreatedUtc    when Logiwa recorded the box (package createdDateTime, UTC). The web VM sends it as "logiwaCreatedAt". Boxes stored before this
                        change stay blank until the web VM sends them again (it does not re-send old boxes, so they stay blank).
  DISPLAY ONLY: nothing reads these columns to decide anything. Other times in the tables stay as they were (local).
  Back-fill of JobCreatedUtc for old rows uses the Windows time zone of the SQL Server PC (so summer time is right); if it cannot be read the
  CURRENT offset is used (can be one hour out for rows from the other half of the year).
  Procedures re-created here (the versions from 004 / 010 plus the lines that mention JobCreatedUtc / LogiwaCreatedUtc): sp_Sync_UpsertOrders, sp_Reset_ByOrder (keeps the Logiwa time),
  sp_Archive_Run (archive keeps both times), sp_List_AvailableJobs and sp_List_Errors (return ReceivedAt, ReceivedUtc, LogiwaAt).
  Run with an admin login in SSMS, after 001..010. Safe to re-run.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'dbo.PackJob', N'JobCreatedUtc') IS NULL
    ALTER TABLE dbo.PackJob ADD JobCreatedUtc datetime2(0) NULL;
IF COL_LENGTH(N'dbo.PackJob', N'LogiwaCreatedUtc') IS NULL
    ALTER TABLE dbo.PackJob ADD LogiwaCreatedUtc datetime2(0) NULL;
IF COL_LENGTH(N'dbo.PackJobArchive', N'JobCreatedUtc') IS NULL
    ALTER TABLE dbo.PackJobArchive ADD JobCreatedUtc datetime2(0) NULL;
IF COL_LENGTH(N'dbo.PackJobArchive', N'LogiwaCreatedUtc') IS NULL
    ALTER TABLE dbo.PackJobArchive ADD LogiwaCreatedUtc datetime2(0) NULL;
GO

-- new rows get the UTC moment of insert (the local one already has a default)
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_PackJob_CreatedUtc')
    ALTER TABLE dbo.PackJob ADD CONSTRAINT DF_PackJob_CreatedUtc DEFAULT (SYSUTCDATETIME()) FOR JobCreatedUtc;
GO

-- back-fill the rows that already exist
DECLARE @tz sysname = NULL;
BEGIN TRY
    EXEC master.dbo.xp_regread N'HKEY_LOCAL_MACHINE', N'SYSTEM\CurrentControlSet\Control\TimeZoneInformation', N'TimeZoneKeyName', @tz OUTPUT;
END TRY
BEGIN CATCH
    SET @tz = NULL;
END CATCH
IF @tz IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @tz) SET @tz = NULL;

IF @tz IS NOT NULL
BEGIN
    UPDATE dbo.PackJob        SET JobCreatedUtc = CAST(JobCreatedDateTime AT TIME ZONE @tz AT TIME ZONE N'UTC' AS datetime2(0)) WHERE JobCreatedUtc IS NULL;
    UPDATE dbo.PackJobArchive SET JobCreatedUtc = CAST(JobCreatedDateTime AT TIME ZONE @tz AT TIME ZONE N'UTC' AS datetime2(0)) WHERE JobCreatedUtc IS NULL;
END
ELSE
BEGIN
    DECLARE @offMin int = DATEDIFF(MINUTE, SYSDATETIME(), SYSUTCDATETIME());
    UPDATE dbo.PackJob        SET JobCreatedUtc = DATEADD(MINUTE, @offMin, JobCreatedDateTime) WHERE JobCreatedUtc IS NULL;
    UPDATE dbo.PackJobArchive SET JobCreatedUtc = DATEADD(MINUTE, @offMin, JobCreatedDateTime) WHERE JobCreatedUtc IS NULL;
END
GO

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
        LabelZpl           nvarchar(max) NULL,
        LogiwaStatus       nvarchar(40)  NULL,
        Kind               nvarchar(10)  NULL,
        Sku                nvarchar(100) NULL,  -- SKU
        Barcode            nvarchar(100) NULL,  -- SKU: item barcode
        LogiwaCreatedUtc   datetime2(0)  NULL   -- DATES: when Logiwa recorded the box (UTC)
    );

    INSERT @in(TrackingNumber, SyncSeq, ShipmentIdentifier, OrderCode, ClientCode, LabelZpl, LogiwaStatus, Kind, Sku, Barcode, LogiwaCreatedUtc)
    SELECT TrackingNumber, SyncSeq, ShipmentIdentifier, OrderCode, ClientCode, NULLIF(LabelZpl, N''), LogiwaStatus, Kind,
           NULLIF(LTRIM(RTRIM(Sku)), N''), NULLIF(LTRIM(RTRIM(Barcode)), N''),
           CAST(TRY_CAST(LogiwaCreatedText AS datetimeoffset(0)) AT TIME ZONE N'UTC' AS datetime2(0))
    FROM (
        SELECT j.TrackingNumber, j.SyncSeq, j.ShipmentIdentifier, j.OrderCode, j.ClientCode, j.LabelZpl, j.LogiwaStatus, j.Kind, j.Sku, j.Barcode, j.LogiwaCreatedText,
               ROW_NUMBER() OVER (PARTITION BY j.TrackingNumber ORDER BY j.SyncSeq DESC) AS rn
        FROM OPENJSON(@Json) WITH (
            SyncSeq            bigint        '$.syncSeq',
            TrackingNumber     nvarchar(50)  '$.trackingNumber',
            ShipmentIdentifier nvarchar(64)  '$.shipmentIdentifier',
            OrderCode          nvarchar(100) '$.orderCode',
            ClientCode         nvarchar(50)  '$.clientCode',
            LabelZpl           nvarchar(max) '$.labelZpl',
            LogiwaStatus       nvarchar(40)  '$.logiwaStatus',
            Kind               nvarchar(10)  '$.kind',
            Sku                nvarchar(100) '$.sku',
            Barcode            nvarchar(100) '$.barcode',
            LogiwaCreatedText  nvarchar(40)  '$.logiwaCreatedAt'   -- ISO text with an offset; converted to UTC
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
        Sku                = COALESCE(i.Sku, p.Sku),         -- SKU
        Barcode            = COALESCE(i.Barcode, p.Barcode), -- SKU: item barcode
        LogiwaCreatedUtc   = COALESCE(i.LogiwaCreatedUtc, p.LogiwaCreatedUtc),   -- DATES
        LabelData    = CASE WHEN p.OrderStatus IN (N'NoLabel', N'Ready', N'OnHold', N'Shipped', N'Cancelled') AND i.LabelZpl IS NOT NULL THEN i.LabelZpl ELSE p.LabelData END,
        LabelDataLen = CASE WHEN p.OrderStatus IN (N'NoLabel', N'Ready', N'OnHold', N'Shipped', N'Cancelled') AND i.LabelZpl IS NOT NULL THEN LEN(i.LabelZpl) ELSE p.LabelDataLen END,
        OrderStatus  = CASE WHEN p.OrderStatus = N'NoLabel' AND i.LabelZpl IS NOT NULL THEN N'Ready' ELSE p.OrderStatus END
    FROM dbo.PackJob p
    JOIN @in i ON i.TrackingNumber = p.OrderNumber
    WHERE p.IsCurrent = 1;
    SET @updated = @@ROWCOUNT;

    -- new orders (never seen locally, not archived); closed / hold ones come without a label and start as Shipped / OnHold
    INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                       LabelData, LabelDataLen, OrderStatus, SyncSeq, IsCurrent, LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, LogiwaCreatedUtc)
    SELECT i.TrackingNumber, i.TrackingNumber, i.TrackingNumber, i.ShipmentIdentifier, i.OrderCode, i.ClientCode,
           i.LabelZpl, LEN(i.LabelZpl),
           CASE WHEN i.Kind = N'cancelled' THEN N'Cancelled'
                WHEN i.Kind = N'closed' THEN N'Shipped'
                WHEN i.Kind = N'hold'   THEN N'OnHold'
                WHEN i.LabelZpl IS NULL THEN N'NoLabel' ELSE N'Ready' END,
           i.SyncSeq, 1, i.LogiwaStatus, CASE WHEN i.LogiwaStatus IS NULL THEN NULL ELSE SYSDATETIME() END, i.Sku, i.Barcode, i.LogiwaCreatedUtc
    FROM @in i
    WHERE NOT EXISTS (SELECT 1 FROM dbo.PackJob p WHERE p.OrderNumber = i.TrackingNumber)
      AND NOT EXISTS (SELECT 1 FROM dbo.PackJobArchive a WHERE a.OrderNumber = i.TrackingNumber);
    SET @inserted = @@ROWCOUNT;

    -- label arrived (or the order is finished / held): close open label requests
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

    -- SHIPPED CHECK: an order finished in Logiwa is never processed again
    IF @fromArchive = 0 AND @status IN (N'Shipped', N'Cancelled')
    BEGIN
        SELECT N'SHIPPED' AS Result, @src AS NewPackJobID;
        RETURN;
    END

    IF @fromArchive = 0 AND @status IN (N'Ready', N'NoLabel', N'OnHold')
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
                           ResetFromPackJobID, ResetByUser, ResetAt,
                           LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, LogiwaCreatedUtc)
        OUTPUT inserted.PackJobID INTO @new
        SELECT JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
               LabelData, LabelDataLen,
               CASE WHEN NULLIF(LabelData, N'') IS NULL THEN N'NoLabel' ELSE N'Ready' END,
               SyncSeq, 1, PackJobID, @User, SYSDATETIME(),
               LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, LogiwaCreatedUtc
        FROM dbo.PackJob WHERE PackJobID = @src;
    ELSE
        INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                           LabelData, LabelDataLen, OrderStatus, SyncSeq, IsCurrent,
                           ResetFromPackJobID, ResetByUser, ResetAt,
                           LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, LogiwaCreatedUtc)
        OUTPUT inserted.PackJobID INTO @new
        SELECT JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
               LabelData, LabelDataLen,
               CASE WHEN NULLIF(LabelData, N'') IS NULL THEN N'NoLabel' ELSE N'Ready' END,
               SyncSeq, 1, PackJobID, @User, SYSDATETIME(),
               LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, LogiwaCreatedUtc
        FROM dbo.PackJobArchive WHERE PackJobID = @src;

    DECLARE @newId int = (SELECT TOP 1 PackJobID FROM @new);

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, Detail)
    VALUES (@newId, @o, N'Reset', @User,
            N'Reset from PackJobID ' + CAST(@src AS nvarchar(20)) + CASE WHEN @fromArchive = 1 THEN N' (archive)' ELSE N'' END);

    COMMIT;

    SELECT N'OK' AS Result, @newId AS NewPackJobID;
END
GO

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
               deleted.PushAttempts, deleted.PushNextAt, deleted.PushLastError, deleted.PushedAt, SYSDATETIME(),
               deleted.LogiwaStatus, deleted.LogiwaCheckedAt, deleted.Sku, deleted.Barcode, deleted.ScannedValue, deleted.JobCreatedUtc, deleted.LogiwaCreatedUtc
        INTO dbo.PackJobArchive(PackJobID, JobCreatedDateTime, JobNumber, OrderNumber, MatchLab,
               ShipmentIdentifier, OrderCode, ClientCode, LabelData, LabelDataLen,
               OrderStatus, ProcessStartedDateTime, ProcessCompletedDateTime, PackID,
               BoxEventID, BoxEventText, FlagBoxLow, HasBoxIssue, BoxIssueComment,
               BoxIssueDateTime, BoxSizeH, BoxSizeL, BoxSizeW, BoxArea,
               WeightCarton, WeightScale, MachineId, ProcessedByUser, SyncSeq,
               IsCurrent, ResetFromPackJobID, ResetByUser, ResetAt, PushStatus,
               PushAttempts, PushNextAt, PushLastError, PushedAt, ArchivedAt,
               LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, ScannedValue, JobCreatedUtc, LogiwaCreatedUtc)
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

CREATE OR ALTER PROCEDURE dbo.sp_List_AvailableJobs
    @Max int = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) PackJobID, OrderNumber AS TrackingNumber, OrderCode, ClientCode, JobCreatedDateTime AS ReceivedAt, JobCreatedUtc AS ReceivedUtc, LogiwaCreatedUtc AS LogiwaAt
    FROM dbo.PackJob
    WHERE IsCurrent = 1 AND OrderStatus = N'Ready'
    ORDER BY JobCreatedDateTime DESC, PackJobID DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_List_Errors
    @Search nvarchar(100) = NULL,
    @From   datetime2(0)  = NULL,
    @To     datetime2(0)  = NULL,
    @Max    int           = 500
AS
BEGIN
    SET NOCOUNT ON;
    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');

    ;WITH Err AS (
        SELECT PackJobID, JobNumber, OrderNumber, OrderCode, OrderStatus, ProcessStartedDateTime, ProcessCompletedDateTime, JobCreatedDateTime, JobCreatedUtc, LogiwaCreatedUtc,
               BoxEventID, BoxEventText, BoxIssueComment, BoxSizeL, BoxSizeW, BoxSizeH, WeightScale, MachineId, ProcessedByUser,
               IsCurrent, CAST(0 AS bit) AS Archived
        FROM dbo.PackJob
        WHERE OrderStatus IN (N'Failed', N'Removed')
        UNION ALL
        SELECT PackJobID, JobNumber, OrderNumber, OrderCode, OrderStatus, ProcessStartedDateTime, ProcessCompletedDateTime, JobCreatedDateTime, JobCreatedUtc, LogiwaCreatedUtc,
               BoxEventID, BoxEventText, BoxIssueComment, BoxSizeL, BoxSizeW, BoxSizeH, WeightScale, MachineId, ProcessedByUser,
               IsCurrent, CAST(1 AS bit)
        FROM dbo.PackJobArchive
        WHERE OrderStatus IN (N'Failed', N'Removed')
    ), Timed AS (
        SELECT *, COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime, JobCreatedDateTime) AS EventAt FROM Err
    )
    SELECT TOP (@Max)
        t.PackJobID, t.OrderNumber AS TrackingNumber, t.JobNumber, t.OrderCode, t.OrderStatus, t.EventAt, t.JobCreatedDateTime AS ReceivedAt, t.JobCreatedUtc AS ReceivedUtc, t.LogiwaCreatedUtc AS LogiwaAt,
        t.BoxEventID AS EventCode,
        COALESCE(NULLIF(t.BoxEventText, N''), NULLIF(t.BoxIssueComment, N''),
                 (SELECT TOP (1) e.Detail FROM dbo.PackJobEvent e
                  WHERE e.PackJobID = t.PackJobID AND e.EventType IN (N'ACK', N'PRN', N'LAB', N'END', N'REM')
                  ORDER BY e.EventId DESC),
                 N'No reason was recorded') AS Reason,
        t.BoxIssueComment AS Note,
        t.BoxSizeL AS LengthMm, t.BoxSizeW AS WidthMm, t.BoxSizeH AS HeightMm, t.WeightScale AS WeightG,
        t.MachineId, t.ProcessedByUser AS Operator, t.IsCurrent, t.Archived
    FROM Timed t
    WHERE (@Search IS NULL OR t.OrderNumber LIKE N'%' + @Search + N'%' OR t.JobNumber LIKE N'%' + @Search + N'%' OR t.OrderCode LIKE N'%' + @Search + N'%')
      AND (@From IS NULL OR t.EventAt >= @From)
      AND (@To   IS NULL OR t.EventAt <  @To)
    ORDER BY t.EventAt DESC, t.PackJobID DESC;
END
GO

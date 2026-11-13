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

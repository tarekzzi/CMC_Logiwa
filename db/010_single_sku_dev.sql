/*
  CMC_Logiwa - single item orders: scan the SKU or the item barcode instead of the tracking number (DEV)
  The web VM sends "sku" and "barcode" for an order that has exactly ONE product line with quantity 1 (otherwise null). They are stored in their own
  columns PackJob.Sku and PackJob.Barcode and are never mixed with the tracking number (OrderNumber / MatchLab stay the tracking number).
  The barcode is the item's UPC in Logiwa (packing station: SKU "SKU2", barcode "unitsku2"); it can be empty when Logiwa has none.
  ENQ: the scanned value is tried as a tracking number first (exactly as before). If that finds no record at all, it is tried as a SKU or a barcode:
  the OLDEST current record with that SKU or barcode that is Ready is claimed (MatchLab is still the tracking number, so the machine still checks the
  printed label). A value that matches nothing Ready is NOT_FOUND and is NOT looked up in Logiwa (no label request) when it is a known SKU/barcode.
  A reset keeps ALL the order's information: sp_Reset_ByOrder now also copies LogiwaStatus, LogiwaCheckedAt, Sku and Barcode (it copied only the
  identifiers and the label before), and sp_Archive_Run moves those four columns to PackJobArchive, so a reset from the archive keeps them too.
  A reset record is a normal Ready record: it is the NEWEST record of its SKU, so a SKU scan takes the older Ready ones first.
  PackJob.ScannedValue records the value that was actually scanned (tracking number, SKU or item barcode) when the machine accepted the carton.
  A SKU/barcode scan NEVER creates a Duplicate event (it cannot be tied to one order): nothing Ready for it = NOT_FOUND, an event "NotFound" only.
  A reset starts with an empty ScannedValue (it is a new run).
  Run with an admin login in SSMS, after 001..009. Safe to re-run (column and index added only if missing, procedures are CREATE OR ALTER).
  sp_Sync_UpsertOrders and sp_ENQ_Get_Data are the 007 versions with the changes marked "SKU".
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'dbo.PackJob', N'Sku') IS NULL
    ALTER TABLE dbo.PackJob ADD Sku nvarchar(100) NULL;   -- single item orders only; archived rows do not keep it
GO

IF COL_LENGTH(N'dbo.PackJob', N'Barcode') IS NULL
    ALTER TABLE dbo.PackJob ADD Barcode nvarchar(100) NULL;   -- item barcode (UPC) of single item orders only
GO

-- the value the operator actually scanned when the machine accepted the carton (tracking number, SKU or item barcode)
IF COL_LENGTH(N'dbo.PackJob', N'ScannedValue') IS NULL
    ALTER TABLE dbo.PackJob ADD ScannedValue nvarchar(100) NULL;
IF COL_LENGTH(N'dbo.PackJobArchive', N'ScannedValue') IS NULL
    ALTER TABLE dbo.PackJobArchive ADD ScannedValue nvarchar(100) NULL;
GO

-- the archive keeps the same information, so a reset from the archive loses nothing
IF COL_LENGTH(N'dbo.PackJobArchive', N'Sku') IS NULL
    ALTER TABLE dbo.PackJobArchive ADD Sku nvarchar(100) NULL;
IF COL_LENGTH(N'dbo.PackJobArchive', N'Barcode') IS NULL
    ALTER TABLE dbo.PackJobArchive ADD Barcode nvarchar(100) NULL;
GO

-- oldest Ready record of a SKU / barcode (ENQ by SKU or barcode)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PackJob_Sku_Ready' AND object_id = OBJECT_ID(N'dbo.PackJob'))
    EXEC(N'CREATE INDEX IX_PackJob_Sku_Ready ON dbo.PackJob(Sku, PackJobID) WHERE Sku IS NOT NULL AND IsCurrent = 1 AND OrderStatus = N''Ready''');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PackJob_Barcode_Ready' AND object_id = OBJECT_ID(N'dbo.PackJob'))
    EXEC(N'CREATE INDEX IX_PackJob_Barcode_Ready ON dbo.PackJob(Barcode, PackJobID) WHERE Barcode IS NOT NULL AND IsCurrent = 1 AND OrderStatus = N''Ready''');
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
        Barcode            nvarchar(100) NULL   -- SKU: item barcode
    );

    INSERT @in(TrackingNumber, SyncSeq, ShipmentIdentifier, OrderCode, ClientCode, LabelZpl, LogiwaStatus, Kind, Sku, Barcode)
    SELECT TrackingNumber, SyncSeq, ShipmentIdentifier, OrderCode, ClientCode, NULLIF(LabelZpl, N''), LogiwaStatus, Kind,
           NULLIF(LTRIM(RTRIM(Sku)), N''), NULLIF(LTRIM(RTRIM(Barcode)), N'')
    FROM (
        SELECT j.TrackingNumber, j.SyncSeq, j.ShipmentIdentifier, j.OrderCode, j.ClientCode, j.LabelZpl, j.LogiwaStatus, j.Kind, j.Sku, j.Barcode,
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
            Barcode            nvarchar(100) '$.barcode'
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
        LabelData    = CASE WHEN p.OrderStatus IN (N'NoLabel', N'Ready', N'OnHold', N'Shipped', N'Cancelled') AND i.LabelZpl IS NOT NULL THEN i.LabelZpl ELSE p.LabelData END,
        LabelDataLen = CASE WHEN p.OrderStatus IN (N'NoLabel', N'Ready', N'OnHold', N'Shipped', N'Cancelled') AND i.LabelZpl IS NOT NULL THEN LEN(i.LabelZpl) ELSE p.LabelDataLen END,
        OrderStatus  = CASE WHEN p.OrderStatus = N'NoLabel' AND i.LabelZpl IS NOT NULL THEN N'Ready' ELSE p.OrderStatus END
    FROM dbo.PackJob p
    JOIN @in i ON i.TrackingNumber = p.OrderNumber
    WHERE p.IsCurrent = 1;
    SET @updated = @@ROWCOUNT;

    -- new orders (never seen locally, not archived); closed / hold ones come without a label and start as Shipped / OnHold
    INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                       LabelData, LabelDataLen, OrderStatus, SyncSeq, IsCurrent, LogiwaStatus, LogiwaCheckedAt, Sku, Barcode)
    SELECT i.TrackingNumber, i.TrackingNumber, i.TrackingNumber, i.ShipmentIdentifier, i.OrderCode, i.ClientCode,
           i.LabelZpl, LEN(i.LabelZpl),
           CASE WHEN i.Kind = N'cancelled' THEN N'Cancelled'
                WHEN i.Kind = N'closed' THEN N'Shipped'
                WHEN i.Kind = N'hold'   THEN N'OnHold'
                WHEN i.LabelZpl IS NULL THEN N'NoLabel' ELSE N'Ready' END,
           i.SyncSeq, 1, i.LogiwaStatus, CASE WHEN i.LogiwaStatus IS NULL THEN NULL ELSE SYSDATETIME() END, i.Sku, i.Barcode
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
        PackID = LEFT(@Counter, 20), MachineId = @MachineId, ProcessedByUser = @User,
        ScannedValue = LEFT(LTRIM(RTRIM(ISNULL(@Barcode, N''))), 100)   -- the value that was scanned
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

    -- SKU: the scan is not a known tracking number (current or archived). Single item orders can be scanned by their SKU or item barcode:
    -- the OLDEST Ready record with that SKU or barcode. Nothing Ready = NOT_FOUND, and Logiwa is NOT asked (no label request).
    IF @id IS NULL AND @bc <> N'' AND NOT EXISTS (SELECT 1 FROM dbo.PackJobArchive WHERE OrderNumber = @bc)
    BEGIN
        ;WITH c AS (
            SELECT TOP (1) *
            FROM dbo.PackJob WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE (Sku = @bc OR Barcode = @bc) AND IsCurrent = 1 AND OrderStatus = N'Ready'
            ORDER BY PackJobID
        )
        UPDATE c
        SET OrderStatus = N'InProgress', ProcessStartedDateTime = SYSDATETIME(),
            PackID = LEFT(@Counter, 20), MachineId = @MachineId, ProcessedByUser = @User,
            ScannedValue = LEFT(LTRIM(RTRIM(ISNULL(@Barcode, N''))), 100)   -- the SKU or item barcode that was scanned
        OUTPUT inserted.PackJobID, inserted.MatchLab INTO @claimed;

        IF EXISTS (SELECT 1 FROM @claimed)
        BEGIN
            DECLARE @skuId int = (SELECT TOP 1 PackJobID FROM @claimed);
            INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
            SELECT @skuId, p.OrderNumber, N'Scan', @User, @MachineId, LEFT(N'ENQ ok by SKU/barcode ' + @bc + N', counter ' + ISNULL(@Counter, N''), 400)
            FROM dbo.PackJob p WHERE p.PackJobID = @skuId;
            SELECT N'OK' AS EnqResult, PackJobID, MatchLab, @bc AS Description FROM @claimed;
            RETURN;
        END

        IF EXISTS (SELECT 1 FROM dbo.PackJob WHERE (Sku = @bc OR Barcode = @bc) AND IsCurrent = 1)
        BEGIN
            -- a known SKU or barcode, but none of its orders is Ready (all packed, on hold, shipped, label missing ...)
            INSERT dbo.PackJobEvent(OrderNumber, EventType, UserName, MachineId, Detail)
            VALUES (@bc, N'NotFound', @User, @MachineId, N'SKU/barcode scan: no Ready order with this item');
            SELECT N'NOT_FOUND' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
                   N'No ready order for this item' AS Description;
            RETURN;
        END
    END

    IF @id IS NOT NULL AND @status = N'NoLabel'
    BEGIN
        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
        VALUES (@id, @bc, N'NoLabel', @User, @MachineId, N'Scan without label');

        -- one open request per tracking number, and not again within 2 minutes of the last one asked for,
        -- whatever happened to it (Sent, or Done because Logiwa does not know the number)
        IF NOT EXISTS (SELECT 1 FROM dbo.LabelRequest
                       WHERE OrderNumber = @bc
                         AND (Status = N'Pending' OR RequestedAt > DATEADD(MINUTE, -2, SYSDATETIME())))
            INSERT dbo.LabelRequest(OrderNumber, MachineId) VALUES (@bc, @MachineId);

        SELECT N'NO_LABEL' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
               N'No label, try again later' AS Description;
        RETURN;
    END

    -- SHIPPED CHECK: Logiwa says this order is already finished (shipped, delivered, cancelled, deleted)
    IF @id IS NOT NULL AND @status = N'OnHold'
    BEGIN
        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
        VALUES (@id, @bc, N'OnHold', @User, @MachineId, N'Scan of an order on hold in Logiwa');
        SELECT N'ON_HOLD' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
               N'On hold in Logiwa' AS Description;
        RETURN;
    END

    IF @id IS NOT NULL AND @status = N'Cancelled'
    BEGIN
        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
        VALUES (@id, @bc, N'Cancelled', @User, @MachineId, N'Scan of an order cancelled in Logiwa');
        SELECT N'CANCELLED' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
               N'Cancelled in Logiwa' AS Description;
        RETURN;
    END

    IF @id IS NOT NULL AND @status = N'Shipped'
    BEGIN
        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
        VALUES (@id, @bc, N'Shipped', @User, @MachineId, N'Scan of an order already finished in Logiwa');
        SELECT N'SHIPPED' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
               LEFT(N'Logiwa: ' + ISNULL((SELECT LogiwaStatus FROM dbo.PackJob WHERE PackJobID = @id), N'finished'), 40) AS Description;
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

    -- Ask the web VM to look this tracking number up in Logiwa (one open request per tracking number, and not again
    -- within 2 minutes of the last one sent). A missed webhook is recovered this way.
    -- SKU: a value that is not in our SQL as a tracking number may be a SKU, and a SKU is never looked up in Logiwa. The web VM looks a
    -- request up as a tracking number, so a SKU scan must not create one. Tracking numbers and SKUs cannot be told apart by their form,
    -- so the request is only skipped when this value is a SKU we know (handled above); an unknown value still asks, as before.
    IF @bc <> N'' AND NOT EXISTS (SELECT 1 FROM dbo.LabelRequest
                   WHERE OrderNumber = @bc
                     AND (Status = N'Pending' OR RequestedAt > DATEADD(MINUTE, -2, SYSDATETIME()))) -- also after a closed (Done) request
        INSERT dbo.LabelRequest(OrderNumber, MachineId) VALUES (@bc, @MachineId);
    SELECT N'NOT_FOUND' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
           N'Not found, try again later' AS Description;
END
GO

/* sp_Reset_ByOrder: the 007 version, but the new record now copies EVERYTHING that describes the order: also LogiwaStatus, LogiwaCheckedAt,
   Sku and Barcode (before, a reset record lost them). The box measurements, scan, label-print and push fields start empty on purpose. */
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
                           LogiwaStatus, LogiwaCheckedAt, Sku, Barcode)
        OUTPUT inserted.PackJobID INTO @new
        SELECT JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
               LabelData, LabelDataLen,
               CASE WHEN NULLIF(LabelData, N'') IS NULL THEN N'NoLabel' ELSE N'Ready' END,
               SyncSeq, 1, PackJobID, @User, SYSDATETIME(),
               LogiwaStatus, LogiwaCheckedAt, Sku, Barcode
        FROM dbo.PackJob WHERE PackJobID = @src;
    ELSE
        INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                           LabelData, LabelDataLen, OrderStatus, SyncSeq, IsCurrent,
                           ResetFromPackJobID, ResetByUser, ResetAt,
                           LogiwaStatus, LogiwaCheckedAt, Sku, Barcode)
        OUTPUT inserted.PackJobID INTO @new
        SELECT JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
               LabelData, LabelDataLen,
               CASE WHEN NULLIF(LabelData, N'') IS NULL THEN N'NoLabel' ELSE N'Ready' END,
               SyncSeq, 1, PackJobID, @User, SYSDATETIME(),
               LogiwaStatus, LogiwaCheckedAt, Sku, Barcode
        FROM dbo.PackJobArchive WHERE PackJobID = @src;

    DECLARE @newId int = (SELECT TOP 1 PackJobID FROM @new);

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, Detail)
    VALUES (@newId, @o, N'Reset', @User,
            N'Reset from PackJobID ' + CAST(@src AS nvarchar(20)) + CASE WHEN @fromArchive = 1 THEN N' (archive)' ELSE N'' END);

    COMMIT;

    SELECT N'OK' AS Result, @newId AS NewPackJobID;
END
GO

/* sp_Archive_Run: the 002 version, but the archive now also receives LogiwaStatus, LogiwaCheckedAt, Sku and Barcode (they were dropped before). */
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
               deleted.LogiwaStatus, deleted.LogiwaCheckedAt, deleted.Sku, deleted.Barcode, deleted.ScannedValue
        INTO dbo.PackJobArchive(PackJobID, JobCreatedDateTime, JobNumber, OrderNumber, MatchLab,
               ShipmentIdentifier, OrderCode, ClientCode, LabelData, LabelDataLen,
               OrderStatus, ProcessStartedDateTime, ProcessCompletedDateTime, PackID,
               BoxEventID, BoxEventText, FlagBoxLow, HasBoxIssue, BoxIssueComment,
               BoxIssueDateTime, BoxSizeH, BoxSizeL, BoxSizeW, BoxArea,
               WeightCarton, WeightScale, MachineId, ProcessedByUser, SyncSeq,
               IsCurrent, ResetFromPackJobID, ResetByUser, ResetAt, PushStatus,
               PushAttempts, PushNextAt, PushLastError, PushedAt, ArchivedAt,
               LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, ScannedValue)
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

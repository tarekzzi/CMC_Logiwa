/*
  CMC_Logiwa - a Failed box scanned again is processed again, with no duplicate alert (DEV)
  Before: any second scan of a tracking number that was processed raised a Duplicate and the operator had to reset it by hand. For a box the machine
  reported as FAILED the box was never made, so that was only extra work.
  Now: sp_ENQ_Get_Data treats a scan of a Failed box as a new run. The failed record is kept in the history (IsCurrent = 0), a new record is created from
  it like a reset (same label, ResetFromPackJobID = the failed one) and is claimed at once, so the machine gets the normal OK answer. Events: Reset
  ("Automatic: failed box scanned again") and Scan. Nothing appears in the Duplicate scans list.
  NOT done (stays as before, so nothing is packed twice by mistake):
    - Completed or Removed boxes, and boxes in progress (still a Duplicate);
    - a Failed box whose Logiwa status is shipped, delivered, cancelled, deleted or on hold (still a Duplicate; the reset button still asks Logiwa);
    - a Failed box with no label.
  This is the 010 version of sp_ENQ_Get_Data plus the block marked FAILED RESCAN. Safe to re-run. Run with an admin login in SSMS, after 001..013.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
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

    -- FAILED RESCAN: a box the machine reported as Failed is scanned again. The box was not made, so this is not a duplicate: the failed record
    -- is replaced by a new one (the same as a reset) that starts straight away. Not done when Logiwa says the order is shipped, delivered,
    -- cancelled, deleted or on hold, or when there is no label to print: those stay as they were (a reset still checks Logiwa first).
    IF @id IS NOT NULL AND @status = N'Failed'
       AND EXISTS (SELECT 1 FROM dbo.PackJob
                   WHERE PackJobID = @id AND NULLIF(LabelData, N'') IS NOT NULL
                     AND (LogiwaStatus IS NULL
                          OR NOT (LogiwaStatus LIKE N'Cancel%' OR LogiwaStatus = N'Deleted' OR LogiwaStatus IN (N'Shipped', N'Delivered') OR LogiwaStatus LIKE N'%Hold%')))
    BEGIN
        SET XACT_ABORT ON;
        BEGIN TRAN;

        UPDATE dbo.PackJob SET IsCurrent = 0 WHERE PackJobID = @id AND IsCurrent = 1 AND OrderStatus = N'Failed';

        IF @@ROWCOUNT = 1
        BEGIN
            INSERT dbo.PackJob(JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                               LabelData, LabelDataLen, OrderStatus, SyncSeq, IsCurrent,
                               ResetFromPackJobID, ResetByUser, ResetAt,
                               LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, LogiwaCreatedUtc,
                               ProcessStartedDateTime, PackID, MachineId, ProcessedByUser, ScannedValue)
            OUTPUT inserted.PackJobID, inserted.MatchLab INTO @claimed
            SELECT JobNumber, OrderNumber, MatchLab, ShipmentIdentifier, OrderCode, ClientCode,
                   LabelData, LabelDataLen, N'InProgress', SyncSeq, 1,
                   PackJobID, @User, SYSDATETIME(),
                   LogiwaStatus, LogiwaCheckedAt, Sku, Barcode, LogiwaCreatedUtc,
                   SYSDATETIME(), LEFT(@Counter, 20), @MachineId, @User, LEFT(LTRIM(RTRIM(ISNULL(@Barcode, N''))), 100)
            FROM dbo.PackJob WHERE PackJobID = @id;

            DECLARE @reId int = (SELECT TOP 1 PackJobID FROM @claimed);
            INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
            VALUES (@reId, @bc, N'Reset', @User, @MachineId, N'Automatic: failed box scanned again (was PackJobID ' + CAST(@id AS nvarchar(20)) + N')');
            INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, UserName, MachineId, Detail)
            VALUES (@reId, @bc, N'Scan', @User, @MachineId, N'ENQ ok after failed box, counter ' + ISNULL(@Counter, N''));

            COMMIT;
            SELECT N'OK' AS EnqResult, PackJobID, MatchLab, @bc AS Description FROM @claimed;
            RETURN;
        END

        ROLLBACK;   -- another scan changed the record first: carry on as before
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

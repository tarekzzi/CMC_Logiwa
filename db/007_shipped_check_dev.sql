/*
  CMC_Logiwa - check against Logiwa: orders finished there (DEV)
  At start-up the app asks the web VM, in the background, about every Ready / NoLabel order that was already stored before
  this start. Orders Logiwa shows as finished (shipped, delivered, cancelled, deleted) get the status 'Shipped' (the name is
  kept for all of these; the Logiwa status text is in LogiwaStatus): a scan is refused with that status, the web sync never turns
  them back into Ready, and they cannot be reset. A reset of a finished box is checked against Logiwa first (in the app).
  Run with an admin login in SSMS, after 001..006. Safe to re-run: columns / constraint changed only if needed,
  procedures are CREATE OR ALTER. Nothing is deleted.
  sp_ENQ_Get_Data and sp_Reset_ByOrder are the 002 versions with one extra block each (marked "SHIPPED CHECK").
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'dbo.PackJob', N'LogiwaStatus') IS NULL
    ALTER TABLE dbo.PackJob ADD LogiwaStatus nvarchar(40) NULL, LogiwaCheckedAt datetime2(0) NULL;
GO
IF COL_LENGTH(N'dbo.PackJobArchive', N'LogiwaStatus') IS NULL
    ALTER TABLE dbo.PackJobArchive ADD LogiwaStatus nvarchar(40) NULL, LogiwaCheckedAt datetime2(0) NULL;
GO

-- allow the new status
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PackJob_Status' AND parent_object_id = OBJECT_ID(N'dbo.PackJob')
           AND definition NOT LIKE N'%Shipped%')
BEGIN
    ALTER TABLE dbo.PackJob DROP CONSTRAINT CK_PackJob_Status;
    ALTER TABLE dbo.PackJob WITH CHECK ADD CONSTRAINT CK_PackJob_Status
        CHECK (OrderStatus IN (N'NoLabel', N'Ready', N'InProgress', N'Completed', N'Failed', N'Removed', N'Shipped'));
END
GO

/* Orders the start-up check asks Logiwa about: not processed yet, with a Logiwa shipment identifier, and stored BEFORE
   @StoredBefore (the start of the app): orders that arrive later come from Logiwa's ReadyToShip webhook and need no check.
   Newest first, in pages: pass the smallest PackJobID of the previous page as @BeforeId (NULL for the first page). */
CREATE OR ALTER PROCEDURE dbo.sp_Shipped_GetToCheck
    @StoredBefore datetime2(0),
    @BeforeId     int = NULL,
    @Max          int = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) PackJobID, OrderNumber AS TrackingNumber, ShipmentIdentifier
    FROM dbo.PackJob
    WHERE IsCurrent = 1 AND OrderStatus IN (N'Ready', N'NoLabel')
      AND NULLIF(LTRIM(RTRIM(ShipmentIdentifier)), N'') IS NOT NULL
      AND JobCreatedDateTime < @StoredBefore
      AND (@BeforeId IS NULL OR PackJobID < @BeforeId)
    ORDER BY PackJobID DESC;
END
GO

/* Records what Logiwa said. Shipped = 1 on a Ready / NoLabel order -> status Shipped (open label requests are closed).
   Shipped = 0 on an order we had marked Shipped -> back to Ready (or NoLabel without a label): Logiwa took it back.
   A finished box (Completed ...) keeps its status; only the Logiwa status text is stored.
   Result: MARKED, CLEARED or NOCHANGE. */
CREATE OR ALTER PROCEDURE dbo.sp_Shipped_Apply
    @PackJobID    int,
    @LogiwaStatus nvarchar(40),
    @Shipped      bit
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @status nvarchar(20), @order nvarchar(50), @label nvarchar(max);
    SELECT @status = OrderStatus, @order = OrderNumber, @label = LabelData
    FROM dbo.PackJob WHERE PackJobID = @PackJobID AND IsCurrent = 1;

    IF @status IS NULL
    BEGIN
        SELECT N'NOCHANGE' AS Result;
        RETURN;
    END

    BEGIN TRAN;

    UPDATE dbo.PackJob SET LogiwaStatus = LEFT(@LogiwaStatus, 40), LogiwaCheckedAt = SYSDATETIME() WHERE PackJobID = @PackJobID;

    -- The machine can scan this order at any moment (the check runs in the background): the status is changed only if it is
    -- still Ready / NoLabel in this very statement, so an order that was just claimed (InProgress) is never touched.
    IF @Shipped = 1 AND @status IN (N'Ready', N'NoLabel')
    BEGIN
        UPDATE dbo.PackJob SET OrderStatus = N'Shipped' WHERE PackJobID = @PackJobID AND OrderStatus IN (N'Ready', N'NoLabel');
        IF @@ROWCOUNT = 0
        BEGIN
            COMMIT;
            SELECT N'NOCHANGE' AS Result;
            RETURN;
        END

        UPDATE dbo.LabelRequest SET Status = N'Done' WHERE OrderNumber = @order AND Status IN (N'Pending', N'Sent');

        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, Detail)
        VALUES (@PackJobID, @order, N'Shipped', LEFT(N'Finished in Logiwa (status ' + ISNULL(@LogiwaStatus, N'?') + N'), will not be offered to the machine', 400));

        COMMIT;
        SELECT N'MARKED' AS Result;
        RETURN;
    END

    IF @Shipped = 0 AND @status = N'Shipped'
    BEGIN
        UPDATE dbo.PackJob
        SET OrderStatus = CASE WHEN NULLIF(@label, N'') IS NULL THEN N'NoLabel' ELSE N'Ready' END
        WHERE PackJobID = @PackJobID AND OrderStatus = N'Shipped';

        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, Detail)
        VALUES (@PackJobID, @order, N'Unshipped', LEFT(N'Logiwa status is now ' + ISNULL(@LogiwaStatus, N'?') + N', offered to the machine again', 400));

        COMMIT;
        SELECT N'CLEARED' AS Result;
        RETURN;
    END

    COMMIT;
    SELECT N'NOCHANGE' AS Result;
END
GO

/* For the check before a reset: the shipment identifier and status of the newest record of a tracking number (current, else archive). */
CREATE OR ALTER PROCEDURE dbo.sp_Shipped_GetByOrder
    @OrderNumber nvarchar(50)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @o nvarchar(50) = LEFT(LTRIM(RTRIM(ISNULL(@OrderNumber, N''))), 50);

    SELECT TOP (1) PackJobID, ShipmentIdentifier, OrderStatus, LogiwaStatus
    FROM (
        SELECT PackJobID, ShipmentIdentifier, OrderStatus, LogiwaStatus, 1 AS Src FROM dbo.PackJob WHERE OrderNumber = @o AND IsCurrent = 1
        UNION ALL
        SELECT PackJobID, ShipmentIdentifier, OrderStatus, LogiwaStatus, 2 FROM dbo.PackJobArchive WHERE OrderNumber = @o
    ) x
    ORDER BY Src, PackJobID DESC;
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
    IF @bc <> N'' AND NOT EXISTS (SELECT 1 FROM dbo.LabelRequest
                   WHERE OrderNumber = @bc
                     AND (Status = N'Pending' OR RequestedAt > DATEADD(MINUTE, -2, SYSDATETIME()))) -- also after a closed (Done) request
        INSERT dbo.LabelRequest(OrderNumber, MachineId) VALUES (@bc, @MachineId);
    SELECT N'NOT_FOUND' AS EnqResult, CAST(NULL AS int) AS PackJobID, CAST(NULL AS nvarchar(100)) AS MatchLab,
           N'Not found, try again later' AS Description;
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
    IF @fromArchive = 0 AND @status = N'Shipped'
    BEGIN
        SELECT N'SHIPPED' AS Result, @src AS NewPackJobID;
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

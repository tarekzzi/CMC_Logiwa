/*
  CMC_Logiwa local database - update 2 (DEV)
  1) sp_ENQ_Get_Data: a scan that is not found also queues a request to the web VM, which asks Logiwa for that tracking number
     (recovers an order whose webhook was missed). One request per tracking number every 2 minutes.
  2) sp_LabelReq_MarkDone: closes a request when Logiwa does not know the tracking number.
  Safe to re-run (CREATE OR ALTER). Nothing is deleted.
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

/* The web VM answered that Logiwa does not know this tracking number: close the request. A new scan makes a new one. */
CREATE OR ALTER PROCEDURE dbo.sp_LabelReq_MarkDone
    @RequestId int,
    @Note      nvarchar(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.LabelRequest SET Status = N'Done', LastError = LEFT(@Note, 400), NextAttemptAt = NULL
    WHERE RequestId = @RequestId AND Status = N'Pending';
END
GO

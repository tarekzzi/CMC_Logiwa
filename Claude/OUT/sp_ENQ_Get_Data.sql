USE [CMC]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- ENQ lookup.
-- Result sets returned to the app:
--   1 row, IsDuplicate = 0 : found    -> PackJobID + MatchLab
--   1 row, IsDuplicate = 1 : duplicate -> nothing is started. Either more than one open PackJob matches,
--                            or the scanned order number was already started (re-scan)
--   0 rows                 : not found
ALTER PROCEDURE [dbo].[sp_ENQ_Get_Data]
    @Barcode   NVARCHAR(20),
    @JobNumber NVARCHAR(20),
    @Counter   NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @PackJobID   INT;
    DECLARE @MatchCount  INT;

    SELECT
        @MatchCount = COUNT(*),
        @PackJobID  = MIN(PackJobID)
    FROM dbo.PackJob WITH (UPDLOCK, ROWLOCK, READPAST)
    WHERE ProcessStartedDateTime IS NULL
      AND
      (
            OrderNumber = @Barcode
            OR
            (
                Qty = 1
                AND
                (
                    ItemCode = @Barcode
                    OR ItemBarcode = @Barcode
                    OR ItemSerialNo = @Barcode
                )
            )
      );

    -- Nothing open: if this order number was already scanned/started, that is a duplicate scan, not "not found"
    IF @MatchCount = 0
       AND EXISTS (SELECT 1 FROM dbo.PackJob WHERE OrderNumber = @Barcode AND ProcessStartedDateTime IS NOT NULL)
    BEGIN
        SELECT
            CAST(NULL AS INT)           AS PackJobID,
            CAST(NULL AS NVARCHAR(150)) AS MatchLab,
            CAST(1 AS INT)              AS IsDuplicate;
        RETURN;
    END

    -- Not found
    IF @MatchCount = 0
    BEGIN
        SELECT
            CAST(NULL AS INT)           AS PackJobID,
            CAST(NULL AS NVARCHAR(150)) AS MatchLab,
            CAST(0 AS INT)              AS IsDuplicate
        WHERE 1 = 0;
        RETURN;
    END

    -- Duplicate: reject without touching any record
    IF @MatchCount > 1
    BEGIN
        SELECT
            CAST(NULL AS INT)           AS PackJobID,
            CAST(NULL AS NVARCHAR(150)) AS MatchLab,
            CAST(1 AS INT)              AS IsDuplicate;
        RETURN;
    END

    -- Exactly one match
    UPDATE dbo.PackJob
    SET
        OrderStatus            = 'ENQ',
        PackID                 = @Counter,
        ProcessStartedDateTime = SYSDATETIME()
    WHERE PackJobID = @PackJobID;

    SELECT
        PackJobID,
        MatchLab,
        CAST(0 AS INT) AS IsDuplicate
    FROM dbo.PackJob
    WHERE PackJobID = @PackJobID;
END
GO

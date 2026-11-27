/*
  CMC_Logiwa - duplicate scans expire (DEV)
  The "Duplicate scans" list only exists to let the operator reset (reprocess) or reject a box that was scanned again. A box now leaves the list
  by itself:
    - 24 hours after its last duplicate scan, OR
    - when somebody signs in to the app again (@Since = the time of the current sign-in: older duplicates are not shown),
  whichever comes first. Nothing is deleted: the Duplicate events stay in the history. Reset or Reject still removes a box at once.
  The newest duplicate is on top. At most @Max rows come back (default 100) so a bad day cannot make the list huge.
  The procedure is the 002 version plus @Since and @Max. Safe to re-run. Run with an admin login in SSMS, after 001..011.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_List_RecentDuplicates
    @Hours int          = 24,
    @Since datetime2(0) = NULL,   -- the current sign-in; NULL = no sign-in limit
    @Max   int          = 100
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @from datetime2(0) = DATEADD(HOUR, -@Hours, SYSDATETIME());
    IF @Since IS NOT NULL AND @Since > @from SET @from = @Since;

    SELECT TOP (@Max)
           e.OrderNumber,
           MAX(e.EventAt) AS LastScan,
           COUNT(*)       AS Scans,
           MAX(p.PackJobID)   AS PackJobID,
           MAX(p.OrderStatus) AS OrderStatus,
           MAX(p.OrderCode)   AS OrderCode
    FROM dbo.PackJobEvent e
    LEFT JOIN dbo.PackJob p ON p.OrderNumber = e.OrderNumber AND p.IsCurrent = 1
    WHERE e.EventType = N'Duplicate'
      AND e.EventAt >= @from
      AND NOT EXISTS (SELECT 1 FROM dbo.PackJobEvent r
                      WHERE r.OrderNumber = e.OrderNumber AND r.EventType IN (N'Reset', N'Reject') AND r.EventAt > e.EventAt)
    GROUP BY e.OrderNumber
    ORDER BY MAX(e.EventAt) DESC, e.OrderNumber;
END
GO

/*
  CMC_Logiwa - History tab: every box with what the machine did and what Logiwa says (DEV)
  READ ONLY: this script only creates a view and two procedures; it changes no data and no table.
  One row per box (tracking number): the current PackJob row, or the newest archived one when the box was archived and not reset since.
  Old rows of a box that was reset are not listed (they are in the box's step history).

  MachineResult  Completed / Failed / Removed / InProgress, or NotPacked (Ready, NoLabel, or finished in Logiwa before the machine packed it)
  LocalStatus    the status of the row on this PC (Ready, NoLabel, InProgress, Completed, Failed, Removed, Shipped, OnHold, Cancelled)
  LogiwaStatus   the status text Logiwa last reported (webhook or the Logiwa check)
  LogiwaState    Logiwa status in four words: Open (ReadyToShip and similar), Shipped (Shipped, Delivered), Cancelled (Cancelled, Deleted), OnHold; Unknown when
                 this PC never stored a Logiwa status. Based on the usual Logiwa status names; a status with another name counts as Open.
  Difference     text when the two sides disagree, else empty. First match wins:
                   - Packed by the machine, but Logiwa says Cancelled / OnHold
                   - Finished in Logiwa (Shipped), not packed by the machine
                   - The machine failed or removed it, still open in Logiwa
                   - In progress for over an hour (the machine never reported the end)
                   - Waiting more than 2 days, still open in Logiwa
  Dates are local time of this PC; the UTC and Logiwa times are the 011 columns.
  Run with an admin login in SSMS, after 001..012. Safe to re-run.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER VIEW dbo.vw_History
AS
WITH Src AS (
    SELECT PackJobID, OrderNumber, JobNumber, OrderCode, ClientCode, OrderStatus, LogiwaStatus, LogiwaCheckedAt,
           JobCreatedDateTime, JobCreatedUtc, LogiwaCreatedUtc, ProcessStartedDateTime, ProcessCompletedDateTime,
           BoxEventText, BoxSizeL, BoxSizeW, BoxSizeH, WeightScale, MachineId, ProcessedByUser, CAST(0 AS bit) AS Archived
    FROM dbo.PackJob
    WHERE IsCurrent = 1
    UNION ALL
    SELECT a.PackJobID, a.OrderNumber, a.JobNumber, a.OrderCode, a.ClientCode, a.OrderStatus, a.LogiwaStatus, a.LogiwaCheckedAt,
           a.JobCreatedDateTime, a.JobCreatedUtc, a.LogiwaCreatedUtc, a.ProcessStartedDateTime, a.ProcessCompletedDateTime,
           a.BoxEventText, a.BoxSizeL, a.BoxSizeW, a.BoxSizeH, a.WeightScale, a.MachineId, a.ProcessedByUser, CAST(1 AS bit)
    FROM dbo.PackJobArchive a
    WHERE a.IsCurrent = 1
      AND NOT EXISTS (SELECT 1 FROM dbo.PackJob p WHERE p.OrderNumber = a.OrderNumber AND p.IsCurrent = 1)
), Cls AS (
    SELECT s.*,
           CASE WHEN s.OrderStatus IN (N'Completed', N'Failed', N'Removed', N'InProgress') THEN s.OrderStatus ELSE N'NotPacked' END AS MachineResult,
           CASE WHEN s.LogiwaStatus IS NULL THEN N'Unknown'
                WHEN s.LogiwaStatus LIKE N'Cancel%' OR s.LogiwaStatus = N'Deleted' THEN N'Cancelled'
                WHEN s.LogiwaStatus IN (N'Shipped', N'Delivered') THEN N'Shipped'
                WHEN s.LogiwaStatus LIKE N'%Hold%' THEN N'OnHold'
                ELSE N'Open' END AS LogiwaState
    FROM Src s
)
SELECT c.PackJobID, c.OrderNumber AS TrackingNumber, c.JobNumber, c.OrderCode, c.ClientCode,
       c.OrderStatus AS LocalStatus, c.MachineResult, c.LogiwaStatus, c.LogiwaState, c.LogiwaCheckedAt,
       CASE WHEN c.MachineResult = N'Completed' AND c.LogiwaState IN (N'Cancelled', N'OnHold')
                 THEN N'Packed by the machine, but Logiwa says ' + c.LogiwaState
            WHEN c.LogiwaState = N'Shipped' AND c.MachineResult <> N'Completed'
                 THEN N'Finished in Logiwa (' + ISNULL(c.LogiwaStatus, N'Shipped') + N'), not packed by the machine'
            WHEN c.MachineResult IN (N'Failed', N'Removed') AND c.LogiwaState = N'Open'
                 THEN N'Machine result ' + c.MachineResult + N', still open in Logiwa'
            WHEN c.MachineResult = N'InProgress' AND DATEDIFF(MINUTE, c.ProcessStartedDateTime, SYSDATETIME()) > 60
                 THEN N'In progress for over an hour (no end from the machine)'
            WHEN c.MachineResult = N'NotPacked' AND c.LogiwaState = N'Open' AND c.JobCreatedDateTime < DATEADD(DAY, -2, SYSDATETIME())
                 THEN N'Waiting more than 2 days, still open in Logiwa'
            ELSE N'' END AS Difference,
       c.JobCreatedDateTime AS ReceivedAt, c.JobCreatedUtc AS ReceivedUtc, c.LogiwaCreatedUtc AS LogiwaAt,
       c.ProcessStartedDateTime AS StartedAt, c.ProcessCompletedDateTime AS CompletedAt,
       c.BoxSizeL AS LengthMm, c.BoxSizeW AS WidthMm, c.BoxSizeH AS HeightMm, c.WeightScale AS WeightG,
       c.BoxEventText AS MachineReason, c.MachineId, c.ProcessedByUser AS Operator, c.Archived
FROM Cls c;
GO

/* The list. @From inclusive, @To exclusive (on the received time); any filter may be NULL. @MachineResult: Completed, Failed, Removed, InProgress,
   NotPacked. @LogiwaState: Open, Shipped, Cancelled, OnHold, Unknown. Newest first. */
CREATE OR ALTER PROCEDURE dbo.sp_List_History
    @Search        nvarchar(100) = NULL,
    @From          datetime2(0)  = NULL,
    @To            datetime2(0)  = NULL,
    @MachineResult nvarchar(20)  = NULL,
    @LogiwaState   nvarchar(20)  = NULL,
    @DiffOnly      bit           = 0,
    @Max           int           = 1000
AS
BEGIN
    SET NOCOUNT ON;
    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');

    SELECT TOP (@Max) h.*
    FROM dbo.vw_History h
    WHERE (@Search IS NULL OR h.TrackingNumber LIKE N'%' + @Search + N'%' OR h.JobNumber LIKE N'%' + @Search + N'%'
                           OR h.OrderCode LIKE N'%' + @Search + N'%' OR h.ClientCode LIKE N'%' + @Search + N'%')
      AND (@From IS NULL OR h.ReceivedAt >= @From)
      AND (@To   IS NULL OR h.ReceivedAt <  @To)
      AND (@MachineResult IS NULL OR h.MachineResult = @MachineResult)
      AND (@LogiwaState   IS NULL OR h.LogiwaState   = @LogiwaState)
      AND (@DiffOnly = 0 OR h.Difference <> N'')
    ORDER BY h.ReceivedAt DESC, h.PackJobID DESC;
END
GO

/* The totals for the same filters (the machine result and Logiwa state filters are left out so the whole picture stays visible). */
CREATE OR ALTER PROCEDURE dbo.sp_History_Counts
    @Search nvarchar(100) = NULL,
    @From   datetime2(0)  = NULL,
    @To     datetime2(0)  = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');

    SELECT COUNT(*)                                                    AS Total,
           SUM(CASE WHEN h.MachineResult = N'Completed'  THEN 1 ELSE 0 END) AS Completed,
           SUM(CASE WHEN h.MachineResult = N'Failed'     THEN 1 ELSE 0 END) AS Failed,
           SUM(CASE WHEN h.MachineResult = N'Removed'    THEN 1 ELSE 0 END) AS Removed,
           SUM(CASE WHEN h.MachineResult = N'InProgress' THEN 1 ELSE 0 END) AS InProgress,
           SUM(CASE WHEN h.MachineResult = N'NotPacked'  THEN 1 ELSE 0 END) AS NotPacked,
           SUM(CASE WHEN h.LogiwaState = N'Open'      THEN 1 ELSE 0 END) AS LogiwaOpen,
           SUM(CASE WHEN h.LogiwaState = N'Shipped'   THEN 1 ELSE 0 END) AS LogiwaShipped,
           SUM(CASE WHEN h.LogiwaState = N'Cancelled' THEN 1 ELSE 0 END) AS LogiwaCancelled,
           SUM(CASE WHEN h.LogiwaState = N'OnHold'    THEN 1 ELSE 0 END) AS LogiwaOnHold,
           SUM(CASE WHEN h.LogiwaState = N'Unknown'   THEN 1 ELSE 0 END) AS LogiwaUnknown,
           SUM(CASE WHEN h.Difference <> N'' THEN 1 ELSE 0 END)           AS Differences
    FROM dbo.vw_History h
    WHERE (@Search IS NULL OR h.TrackingNumber LIKE N'%' + @Search + N'%' OR h.JobNumber LIKE N'%' + @Search + N'%'
                           OR h.OrderCode LIKE N'%' + @Search + N'%' OR h.ClientCode LIKE N'%' + @Search + N'%')
      AND (@From IS NULL OR h.ReceivedAt >= @From)
      AND (@To   IS NULL OR h.ReceivedAt <  @To);
END
GO

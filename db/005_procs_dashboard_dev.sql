/*
  CMC_Logiwa - dashboard: speed and throughput (DEV)
  Run AFTER 004, with an admin login in SSMS. Safe to re-run (CREATE OR ALTER). Nothing is changed or deleted.

  Processing time of a box = ProcessCompletedDateTime - ProcessStartedDateTime
                           = from the accepted scan (ENQ) to the END message of the machine.
  Times are local time of the packing PC SQL instance (SYSDATETIME), the same as the other screens.
  Only boxes with a plausible time (0 s .. 1 hour) go into the averages; a box that sat on the machine for hours
  (stopped line, reset) would otherwise ruin the average.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* Everything the machine finished or gave up on, current and archived, one row per attempt. */
CREATE OR ALTER VIEW dbo.vw_PackWork
AS
    SELECT PackJobID, OrderNumber, OrderCode, ClientCode, OrderStatus, MachineId, ProcessedByUser,
           ProcessStartedDateTime AS StartedAt,
           COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime) AS At,
           CASE WHEN ProcessStartedDateTime IS NOT NULL AND ProcessCompletedDateTime IS NOT NULL
                     AND ProcessCompletedDateTime >= ProcessStartedDateTime
                     AND DATEDIFF(SECOND, ProcessStartedDateTime, ProcessCompletedDateTime) <= 3600
                THEN DATEDIFF(SECOND, ProcessStartedDateTime, ProcessCompletedDateTime) END AS DurationSec,
           BoxSizeL, BoxSizeW, BoxSizeH, WeightScale,
           CAST(0 AS bit) AS Archived
    FROM dbo.PackJob
    WHERE OrderStatus IN (N'Completed', N'Failed', N'Removed')
    UNION ALL
    SELECT PackJobID, OrderNumber, OrderCode, ClientCode, OrderStatus, MachineId, ProcessedByUser,
           ProcessStartedDateTime,
           COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime),
           CASE WHEN ProcessStartedDateTime IS NOT NULL AND ProcessCompletedDateTime IS NOT NULL
                     AND ProcessCompletedDateTime >= ProcessStartedDateTime
                     AND DATEDIFF(SECOND, ProcessStartedDateTime, ProcessCompletedDateTime) <= 3600
                THEN DATEDIFF(SECOND, ProcessStartedDateTime, ProcessCompletedDateTime) END,
           BoxSizeL, BoxSizeW, BoxSizeH, WeightScale,
           CAST(1 AS bit)
    FROM dbo.PackJobArchive
    WHERE OrderStatus IN (N'Completed', N'Failed', N'Removed');
GO

/* Clients that have worked boxes (for the filter list). */
CREATE OR ALTER PROCEDURE dbo.sp_Dashboard_Clients
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DISTINCT ClientCode FROM dbo.vw_PackWork WHERE ClientCode IS NOT NULL AND ClientCode <> N'' ORDER BY ClientCode;
END
GO

/* Totals for the chosen period and filters.
   @From inclusive, @To exclusive. @Search = part of a tracking number, order code or job number. @Client = exact client code or NULL. */
CREATE OR ALTER PROCEDURE dbo.sp_Dashboard_Summary
    @From   datetime2(0),
    @To     datetime2(0),
    @Search nvarchar(100) = NULL,
    @Client nvarchar(50)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH W AS (
        SELECT * FROM dbo.vw_PackWork
        WHERE At >= @From AND At < @To
          AND (@Client IS NULL OR ClientCode = @Client)
          AND (@Search IS NULL OR OrderNumber LIKE N'%' + @Search + N'%' OR OrderCode LIKE N'%' + @Search + N'%'
               OR CAST(PackJobID AS nvarchar(20)) = @Search)
    ),
    Hr AS (   -- boxes completed per clock hour, only hours in which the machine finished something
        SELECT DATEADD(HOUR, DATEDIFF(HOUR, 0, At), 0) AS H, COUNT(*) AS N
        FROM W WHERE OrderStatus = N'Completed' GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, At), 0)
    )
    SELECT
        Completed   = (SELECT COUNT(*) FROM W WHERE OrderStatus = N'Completed'),
        Failed      = (SELECT COUNT(*) FROM W WHERE OrderStatus = N'Failed'),
        Removed     = (SELECT COUNT(*) FROM W WHERE OrderStatus = N'Removed'),
        AvgSec      = (SELECT CAST(AVG(CAST(DurationSec AS float)) AS float) FROM W WHERE OrderStatus = N'Completed' AND DurationSec IS NOT NULL),
        MinSec      = (SELECT MIN(DurationSec) FROM W WHERE OrderStatus = N'Completed'),
        MaxSec      = (SELECT MAX(DurationSec) FROM W WHERE OrderStatus = N'Completed'),
        FirstAt     = (SELECT MIN(At) FROM W WHERE OrderStatus = N'Completed'),
        LastAt      = (SELECT MAX(At) FROM W WHERE OrderStatus = N'Completed'),
        ActiveHours = (SELECT COUNT(*) FROM Hr),
        PeakHour    = (SELECT TOP (1) H FROM Hr ORDER BY N DESC, H DESC),
        PeakCount   = (SELECT MAX(N) FROM Hr);
END
GO

/* One row per hour (@Bucket = 'H') or per day (@Bucket = 'D') that has work; the app fills the empty ones.
   Avg seconds is the average processing time of the boxes completed in that bucket. */
CREATE OR ALTER PROCEDURE dbo.sp_Dashboard_Series
    @From   datetime2(0),
    @To     datetime2(0),
    @Bucket char(1) = 'D',
    @Search nvarchar(100) = NULL,
    @Client nvarchar(50)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH W AS (
        SELECT *, CASE WHEN @Bucket = 'H' THEN DATEADD(HOUR, DATEDIFF(HOUR, 0, At), 0)
                       ELSE DATEADD(DAY, DATEDIFF(DAY, 0, At), 0) END AS B
        FROM dbo.vw_PackWork
        WHERE At >= @From AND At < @To
          AND (@Client IS NULL OR ClientCode = @Client)
          AND (@Search IS NULL OR OrderNumber LIKE N'%' + @Search + N'%' OR OrderCode LIKE N'%' + @Search + N'%'
               OR CAST(PackJobID AS nvarchar(20)) = @Search)
    )
    SELECT B AS BucketStart,
           Completed = SUM(CASE WHEN OrderStatus = N'Completed' THEN 1 ELSE 0 END),
           Problems  = SUM(CASE WHEN OrderStatus <> N'Completed' THEN 1 ELSE 0 END),
           AvgSec    = AVG(CASE WHEN OrderStatus = N'Completed' AND DurationSec IS NOT NULL THEN CAST(DurationSec AS float) END)
    FROM W
    GROUP BY B
    ORDER BY B;
END
GO

/* The boxes behind the numbers, newest first. @Status = Completed / Failed / Removed or NULL for all. */
CREATE OR ALTER PROCEDURE dbo.sp_List_Processed
    @From   datetime2(0),
    @To     datetime2(0),
    @Search nvarchar(100) = NULL,
    @Client nvarchar(50)  = NULL,
    @Status nvarchar(20)  = NULL,
    @Max    int = 500
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Max) PackJobID, OrderNumber AS TrackingNumber, OrderCode, ClientCode, OrderStatus,
           At AS FinishedAt, DurationSec, MachineId, ProcessedByUser AS Operator, BoxSizeL, BoxSizeW, BoxSizeH, WeightScale, Archived
    FROM dbo.vw_PackWork
    WHERE At >= @From AND At < @To
      AND (@Client IS NULL OR ClientCode = @Client)
      AND (@Status IS NULL OR OrderStatus = @Status)
      AND (@Search IS NULL OR OrderNumber LIKE N'%' + @Search + N'%' OR OrderCode LIKE N'%' + @Search + N'%'
           OR CAST(PackJobID AS nvarchar(20)) = @Search)
    ORDER BY At DESC, PackJobID DESC;
END
GO

/* Boxes per operator for the period (the user logged in when the box was scanned). */
CREATE OR ALTER PROCEDURE dbo.sp_Dashboard_Operators
    @From   datetime2(0),
    @To     datetime2(0),
    @Search nvarchar(100) = NULL,
    @Client nvarchar(50)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT ISNULL(NULLIF(ProcessedByUser, N''), N'(unknown)') AS Operator,
           Completed = SUM(CASE WHEN OrderStatus = N'Completed' THEN 1 ELSE 0 END),
           Problems  = SUM(CASE WHEN OrderStatus <> N'Completed' THEN 1 ELSE 0 END),
           AvgSec    = AVG(CASE WHEN OrderStatus = N'Completed' AND DurationSec IS NOT NULL THEN CAST(DurationSec AS float) END)
    FROM dbo.vw_PackWork
    WHERE At >= @From AND At < @To
      AND (@Client IS NULL OR ClientCode = @Client)
      AND (@Search IS NULL OR OrderNumber LIKE N'%' + @Search + N'%' OR OrderCode LIKE N'%' + @Search + N'%'
           OR CAST(PackJobID AS nvarchar(20)) = @Search)
    GROUP BY ISNULL(NULLIF(ProcessedByUser, N''), N'(unknown)')
    ORDER BY Completed DESC;
END
GO

/* Stops: quiet spells between two completed boxes that last at least @MinMinutes.
   Gaps longer than @IgnoreAboveMinutes (default 3 h) are taken as the end of a shift, not a stop.
   The gap is measured from the box that finished before to the next box that finished. */
CREATE OR ALTER PROCEDURE dbo.sp_Dashboard_Stops
    @From   datetime2(0),
    @To     datetime2(0),
    @MinMinutes int = 10,
    @IgnoreAboveMinutes int = 180,
    @Search nvarchar(100) = NULL,
    @Client nvarchar(50)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH W AS (
        SELECT At, LAG(At) OVER (ORDER BY At, PackJobID) AS PrevAt
        FROM dbo.vw_PackWork
        WHERE OrderStatus = N'Completed' AND At >= @From AND At < @To
          AND (@Client IS NULL OR ClientCode = @Client)
          AND (@Search IS NULL OR OrderNumber LIKE N'%' + @Search + N'%' OR OrderCode LIKE N'%' + @Search + N'%'
               OR CAST(PackJobID AS nvarchar(20)) = @Search)
    )
    SELECT PrevAt AS StoppedAt, At AS ResumedAt, DATEDIFF(SECOND, PrevAt, At) AS Seconds
    FROM W
    WHERE PrevAt IS NOT NULL
      AND DATEDIFF(SECOND, PrevAt, At) >= @MinMinutes * 60
      AND DATEDIFF(SECOND, PrevAt, At) <= @IgnoreAboveMinutes * 60
    ORDER BY PrevAt DESC;
END
GO

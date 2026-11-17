/*
  CMC_Logiwa local database - stored procedures for the Settings / Overview / Errors screens (DEV)
    sp_User_List, sp_User_SetActive          Settings > Users
    sp_Dashboard_Counts, sp_List_AvailableJobs   Overview tab
    sp_List_Errors, sp_List_PackJobEvents    Errors tab
  Reads PackJob and PackJobArchive together, so a count or a search does not change when the daily archive runs.
  The app login already has EXECUTE on schema dbo. Safe to re-run (CREATE OR ALTER). Nothing is changed or deleted.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ================================================================ USERS */
CREATE OR ALTER PROCEDURE dbo.sp_User_List
AS
BEGIN
    SET NOCOUNT ON;
    SELECT UserId, Username, IsActive, CreatedAt, LastLoginAt FROM dbo.Users ORDER BY Username;
END
GO

/* Result: OK, NOT_FOUND, or LAST_ACTIVE (the last active user cannot be switched off: nobody could log in any more). */
CREATE OR ALTER PROCEDURE dbo.sp_User_SetActive
    @Username nvarchar(50),
    @IsActive bit
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username = @Username)
    BEGIN
        SELECT N'NOT_FOUND' AS Result;
        RETURN;
    END

    IF @IsActive = 0
       AND EXISTS (SELECT 1 FROM dbo.Users WHERE Username = @Username AND IsActive = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username <> @Username AND IsActive = 1)
    BEGIN
        SELECT N'LAST_ACTIVE' AS Result;
        RETURN;
    END

    UPDATE dbo.Users SET IsActive = @IsActive WHERE Username = @Username;
    SELECT N'OK' AS Result;
END
GO

/* ================================================================ OVERVIEW */
/* Jobs processed = finished boxes (Completed) by the time they finished; problems = Failed and Removed by the time they were last
   worked on. The caller passes the start of today, of this week and of this month (local time). Available = ready to scan now. */
CREATE OR ALTER PROCEDURE dbo.sp_Dashboard_Counts
    @TodayStart datetime2(0),
    @WeekStart  datetime2(0),
    @MonthStart datetime2(0)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @From datetime2(0) = (SELECT MIN(d) FROM (VALUES (@TodayStart), (@WeekStart), (@MonthStart)) AS v(d));

    ;WITH Work AS (
        SELECT OrderStatus, COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime) AS At
        FROM dbo.PackJob
        WHERE OrderStatus IN (N'Completed', N'Failed', N'Removed') AND COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime) >= @From
        UNION ALL
        SELECT OrderStatus, COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime)
        FROM dbo.PackJobArchive
        WHERE OrderStatus IN (N'Completed', N'Failed', N'Removed') AND COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime) >= @From
    )
    SELECT
        ProcessedToday = ISNULL(SUM(CASE WHEN OrderStatus = N'Completed' AND At >= @TodayStart THEN 1 ELSE 0 END), 0),
        ProcessedWeek  = ISNULL(SUM(CASE WHEN OrderStatus = N'Completed' AND At >= @WeekStart  THEN 1 ELSE 0 END), 0),
        ProcessedMonth = ISNULL(SUM(CASE WHEN OrderStatus = N'Completed' AND At >= @MonthStart THEN 1 ELSE 0 END), 0),
        ProblemsToday  = ISNULL(SUM(CASE WHEN OrderStatus <> N'Completed' AND At >= @TodayStart THEN 1 ELSE 0 END), 0),
        ProblemsWeek   = ISNULL(SUM(CASE WHEN OrderStatus <> N'Completed' AND At >= @WeekStart  THEN 1 ELSE 0 END), 0),
        ProblemsMonth  = ISNULL(SUM(CASE WHEN OrderStatus <> N'Completed' AND At >= @MonthStart THEN 1 ELSE 0 END), 0),
        Available  = (SELECT COUNT(*) FROM dbo.PackJob WHERE IsCurrent = 1 AND OrderStatus = N'Ready'),
        NoLabel    = (SELECT COUNT(*) FROM dbo.PackJob WHERE IsCurrent = 1 AND OrderStatus = N'NoLabel'),
        InProgress = (SELECT COUNT(*) FROM dbo.PackJob WHERE IsCurrent = 1 AND OrderStatus = N'InProgress')
    FROM Work;
END
GO

/* The jobs a scan would accept right now, newest first. */
CREATE OR ALTER PROCEDURE dbo.sp_List_AvailableJobs
    @Max int = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) PackJobID, OrderNumber AS TrackingNumber, OrderCode, ClientCode, JobCreatedDateTime AS ReceivedAt
    FROM dbo.PackJob
    WHERE IsCurrent = 1 AND OrderStatus = N'Ready'
    ORDER BY JobCreatedDateTime DESC, PackJobID DESC;
END
GO

/* ================================================================ ERRORS */
/* Boxes that did not pack: Failed (machine said no, label did not match, ...) or Removed. Reason = what the machine reported
   (BoxEventText, from the Events table), else the box issue comment, else the detail of the last machine step.
   @Search matches the tracking number / job number / order code (part of it is enough); @From inclusive, @To exclusive; any may be NULL. */
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
        SELECT PackJobID, JobNumber, OrderNumber, OrderCode, OrderStatus, ProcessStartedDateTime, ProcessCompletedDateTime, JobCreatedDateTime,
               BoxEventID, BoxEventText, BoxIssueComment, BoxSizeL, BoxSizeW, BoxSizeH, WeightScale, MachineId, ProcessedByUser,
               IsCurrent, CAST(0 AS bit) AS Archived
        FROM dbo.PackJob
        WHERE OrderStatus IN (N'Failed', N'Removed')
        UNION ALL
        SELECT PackJobID, JobNumber, OrderNumber, OrderCode, OrderStatus, ProcessStartedDateTime, ProcessCompletedDateTime, JobCreatedDateTime,
               BoxEventID, BoxEventText, BoxIssueComment, BoxSizeL, BoxSizeW, BoxSizeH, WeightScale, MachineId, ProcessedByUser,
               IsCurrent, CAST(1 AS bit)
        FROM dbo.PackJobArchive
        WHERE OrderStatus IN (N'Failed', N'Removed')
    ), Timed AS (
        SELECT *, COALESCE(ProcessCompletedDateTime, ProcessStartedDateTime, JobCreatedDateTime) AS EventAt FROM Err
    )
    SELECT TOP (@Max)
        t.PackJobID, t.OrderNumber AS TrackingNumber, t.JobNumber, t.OrderCode, t.OrderStatus, t.EventAt,
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

/* Everything that happened to one job, oldest first (also from the archive): scans, machine steps and their details. */
CREATE OR ALTER PROCEDURE dbo.sp_List_PackJobEvents
    @PackJobID int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT EventId, EventAt, EventType, Detail, UserName, MachineId
    FROM (
        SELECT EventId, EventAt, EventType, Detail, UserName, MachineId FROM dbo.PackJobEvent WHERE PackJobID = @PackJobID
        UNION ALL
        SELECT EventId, EventAt, EventType, Detail, UserName, MachineId FROM dbo.PackJobEventArchive WHERE PackJobID = @PackJobID
    ) x
    ORDER BY EventId;
END
GO

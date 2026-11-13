/*
  CMC_Logiwa local database - machine procedures (DEV)
  Ported from the V1 definitions (DOC\v1-procs.sql) with the same rules, adapted to the new table:
    - V1 statuses ENQ/ACK/PRN/END/Failed ACK/Failed PRN/Failed End/REMOVED become the fixed set
      InProgress / Completed / Failed / Removed; the step detail goes to PackJobEvent.
    - sp_Update_END also queues the result for the web VM (PushStatus = Pending) on a successful END.
    - Nothing here talks to Logiwa.
  Safe to re-run (CREATE OR ALTER). Run after 001 and 002.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ACK from the machine: '1' ok (nothing to change), '0' failed. */
CREATE OR ALTER PROCEDURE dbo.sp_Update_ACK_Status
    @PackID     NVARCHAR(20),
    @PackJobID  INT,
    @ACK_Status NVARCHAR(1)
AS
BEGIN
    SET NOCOUNT ON;

    IF @ACK_Status = N'0'
        UPDATE dbo.PackJob SET OrderStatus = N'Failed' WHERE PackJobID = @PackJobID;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'ACK', MachineId,
           CASE WHEN @ACK_Status = N'1' THEN N'ACK ok'
                WHEN @ACK_Status = N'0' THEN N'ACK failed'
                ELSE N'ACK status ' + ISNULL(@ACK_Status, N'?') END
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;
END
GO

/* Label to print. No row is returned (so no PRN is sent) when the label is missing. */
CREATE OR ALTER PROCEDURE dbo.sp_Get_PRN_Data
    @PackJobID INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @LabelData NVARCHAR(MAX);

    SELECT @LabelData = LabelData FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    IF @LabelData IS NULL OR LTRIM(RTRIM(@LabelData)) = N''
    BEGIN
        UPDATE dbo.PackJob
        SET HasBoxIssue = 1,
            BoxIssueComment = N'LabelData missing - PRN not sent',
            BoxIssueDateTime = SYSDATETIME()
        WHERE PackJobID = @PackJobID;

        INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
        SELECT PackJobID, OrderNumber, N'PRN', MachineId, N'Label missing, PRN not sent'
        FROM dbo.PackJob WHERE PackJobID = @PackJobID;

        SELECT CAST(NULL AS NVARCHAR(20)) AS PackID, CAST(NULL AS NVARCHAR(MAX)) AS LabelData WHERE 1 = 0;
        RETURN;
    END

    SELECT PackID, LabelData FROM dbo.PackJob WHERE PackJobID = @PackJobID;
END
GO

/* prn result from the machine: '1' printed, '0' failed. */
CREATE OR ALTER PROCEDURE dbo.sp_Update_PRN
    @PackJobID INT,
    @PRNResult NVARCHAR(1)
AS
BEGIN
    SET NOCOUNT ON;

    IF @PRNResult = N'0'
        UPDATE dbo.PackJob SET OrderStatus = N'Failed' WHERE PackJobID = @PackJobID;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'PRN', MachineId,
           CASE WHEN @PRNResult = N'1' THEN N'Print ok'
                WHEN @PRNResult = N'0' THEN N'Print failed'
                ELSE N'Print result ' + ISNULL(@PRNResult, N'?') END
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;
END
GO

/* LAB (label check and measurement). Returns LABResults (1 = ok) and MatchLab, as V1 did. */
CREATE OR ALTER PROCEDURE dbo.sp_Update_LAB
    @PackJobID       NVARCHAR(20),
    @LabStatus       INT,
    @LabEvent        NVARCHAR(20),
    @LabSizeH        INT,
    @LabSizeL        INT,
    @LabSizeW        INT,
    @LabArea         INT,
    @LabWeightCarton INT,
    @LabWeightScale  INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @id INT = TRY_CONVERT(INT, @PackJobID);
    DECLARE @EventText NVARCHAR(100), @LABResults INT, @HasBoxIssue BIT, @MatchLab NVARCHAR(100), @found INT = 0;

    SELECT TOP (1) @EventText = EventText FROM dbo.Events WHERE EventCode = @LabEvent;
    SET @EventText = ISNULL(@EventText, N'Unknown Event');

    SELECT @HasBoxIssue = HasBoxIssue, @MatchLab = MatchLab, @found = 1
    FROM dbo.PackJob WHERE PackJobID = @id;

    IF @found = 0
    BEGIN
        SELECT CAST(0 AS INT) AS LABResults, CAST(NULL AS NVARCHAR(100)) AS MatchLab;   -- unknown carton: reject
        RETURN;
    END

    IF @HasBoxIssue = 1
        SET @LABResults = 0;
    ELSE
        SET @LABResults = CASE WHEN @LabStatus = 1 THEN 1 ELSE 0 END;

    UPDATE dbo.PackJob
    SET OrderStatus  = CASE WHEN @LABResults = 1 THEN OrderStatus ELSE N'Failed' END,
        BoxEventID   = @LabEvent,
        BoxEventText = @EventText,
        BoxSizeH = @LabSizeH,
        BoxSizeL = @LabSizeL,
        BoxSizeW = @LabSizeW,
        BoxArea  = @LabArea,
        WeightCarton = @LabWeightCarton,
        WeightScale  = @LabWeightScale
    WHERE PackJobID = @id;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'LAB', MachineId,
           LEFT((CASE WHEN @LABResults = 1 THEN N'Label ok' ELSE N'Label failed' END) + N': ' + @EventText, 400)
    FROM dbo.PackJob WHERE PackJobID = @id;

    SELECT @LABResults AS LABResults, @MatchLab AS MatchLab;
END
GO

/* END (carton finished). Stores the measured size/weight. A successful END is queued for the web VM
   (PushStatus = Pending, once). Returns FinalResult (1 = success).                                   */
CREATE OR ALTER PROCEDURE dbo.sp_Update_END
    @PackJobID       INT,
    @EndStatus       INT,
    @EndEvent        NVARCHAR(20),
    @EndSizeH        INT,
    @EndSizeL        INT,
    @EndSizeW        INT,
    @EndArea         INT,
    @EndWeightCarton INT,
    @EndWeightScale  INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EventText NVARCHAR(100), @HasBoxIssue BIT, @FinalResult INT, @push NVARCHAR(10), @found INT = 0;

    SELECT @HasBoxIssue = ISNULL(HasBoxIssue, 0), @push = PushStatus, @found = 1
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    IF @found = 0
    BEGIN
        SELECT CAST(0 AS INT) AS FinalResult;
        RETURN;
    END

    -- the result was already sent to the web VM: a repeated END must not change it
    IF @push = N'Sent'
    BEGIN
        SELECT CAST(1 AS INT) AS FinalResult;
        RETURN;
    END

    SELECT TOP (1) @EventText = EventText FROM dbo.Events WHERE EventCode = @EndEvent;
    SET @EventText = ISNULL(@EventText, N'Unknown Event');

    IF @HasBoxIssue = 1
        SET @FinalResult = 0;
    ELSE
        SET @FinalResult = CASE WHEN @EndStatus = 1 THEN 1 ELSE 0 END;

    UPDATE dbo.PackJob
    SET OrderStatus  = CASE WHEN @FinalResult = 1 THEN N'Completed' ELSE N'Failed' END,
        BoxEventID   = @EndEvent,
        BoxEventText = @EventText,
        BoxSizeH = @EndSizeH,
        BoxSizeL = @EndSizeL,
        BoxSizeW = @EndSizeW,
        BoxArea  = @EndArea,
        WeightCarton = @EndWeightCarton,
        WeightScale  = @EndWeightScale,
        ProcessCompletedDateTime = SYSDATETIME(),
        PushStatus   = CASE WHEN @FinalResult = 1 THEN N'Pending' ELSE PushStatus END,
        PushAttempts = CASE WHEN @FinalResult = 1 THEN 0 ELSE PushAttempts END,
        PushNextAt   = NULL,
        PushLastError = NULL
    WHERE PackJobID = @PackJobID;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'END', MachineId,
           LEFT((CASE WHEN @FinalResult = 1 THEN N'End ok' ELSE N'End failed' END) + N': ' + @EventText, 400)
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;

    SELECT @FinalResult AS FinalResult;
END
GO

/* REM (carton removed from the machine). Local only. */
CREATE OR ALTER PROCEDURE dbo.sp_Update_REM
    @PackJobID INT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.PackJob
    SET OrderStatus = N'Removed',
        ProcessCompletedDateTime = SYSDATETIME()
    WHERE PackJobID = @PackJobID;

    INSERT dbo.PackJobEvent(PackJobID, OrderNumber, EventType, MachineId, Detail)
    SELECT PackJobID, OrderNumber, N'REM', MachineId, N'Carton removed'
    FROM dbo.PackJob WHERE PackJobID = @PackJobID;
END
GO

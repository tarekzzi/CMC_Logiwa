/*
  CMC_Logiwa - machine settings in the database (DEV)
  Settings > Machine: machine ID, IP address, port and "reconnect automatically", one row per database
  (so DEV and PRO each keep their own). Every change is logged in dbo.MachineSettingsLog.
  Run with an admin login in SSMS, after 001..005. Safe to re-run: tables are created only if missing, the first row
  is inserted only if missing, procedures are CREATE OR ALTER. Nothing is changed or deleted.
  The app login already has EXECUTE on schema dbo.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.MachineSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MachineSettings(
        Id            int           NOT NULL CONSTRAINT PK_MachineSettings PRIMARY KEY CONSTRAINT CK_MachineSettings_One CHECK (Id = 1),
        MachineId     nvarchar(20)  NOT NULL,
        IpAddress     nvarchar(100) NOT NULL,
        Port          int           NOT NULL CONSTRAINT CK_MachineSettings_Port CHECK (Port BETWEEN 1 AND 65535),
        AutoReconnect bit           NOT NULL CONSTRAINT DF_MachineSettings_Auto DEFAULT (1),
        UpdatedAt     datetime2(0)  NOT NULL CONSTRAINT DF_MachineSettings_At DEFAULT (SYSDATETIME()),
        UpdatedBy     nvarchar(50)  NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.MachineSettings WHERE Id = 1)
    INSERT dbo.MachineSettings(Id, MachineId, IpAddress, Port, AutoReconnect, UpdatedBy)
    VALUES (1, N'002', N'10.2.1.201', 9200, 1, N'setup script');
GO

IF OBJECT_ID(N'dbo.MachineSettingsLog', N'U') IS NULL
CREATE TABLE dbo.MachineSettingsLog(
    LogId      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MachineSettingsLog PRIMARY KEY,
    ChangedAt  datetime2(0)  NOT NULL CONSTRAINT DF_MachineSettingsLog_At DEFAULT (SYSDATETIME()),
    ChangedBy  nvarchar(50)  NULL,
    Detail     nvarchar(400) NOT NULL
);
GO

/* The one row. */
CREATE OR ALTER PROCEDURE dbo.sp_MachineSettings_Get
AS
BEGIN
    SET NOCOUNT ON;
    SELECT MachineId, IpAddress, Port, AutoReconnect, UpdatedAt, UpdatedBy FROM dbo.MachineSettings WHERE Id = 1;
END
GO

/* Saves the settings and logs what changed. Result: OK, or an error text starting with BAD_ (nothing saved).
   The machine ID goes into every message as "<id>|TYPE|..." so it may not contain the pipe (the app also refuses control characters). */
CREATE OR ALTER PROCEDURE dbo.sp_MachineSettings_Save
    @MachineId     nvarchar(20),
    @IpAddress     nvarchar(100),
    @Port          int,
    @AutoReconnect bit,
    @User          nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @MachineId = LTRIM(RTRIM(@MachineId));
    SET @IpAddress = LTRIM(RTRIM(@IpAddress));

    IF @MachineId IS NULL OR @MachineId = N'' OR CHARINDEX(N'|', @MachineId) > 0
    BEGIN SELECT N'BAD_MACHINE_ID' AS Result; RETURN; END

    IF @IpAddress IS NULL OR @IpAddress = N''
    BEGIN SELECT N'BAD_IP' AS Result; RETURN; END

    IF @Port IS NULL OR @Port < 1 OR @Port > 65535
    BEGIN SELECT N'BAD_PORT' AS Result; RETURN; END

    DECLARE @oldId nvarchar(20), @oldIp nvarchar(100), @oldPort int, @oldAuto bit;
    SELECT @oldId = MachineId, @oldIp = IpAddress, @oldPort = Port, @oldAuto = AutoReconnect FROM dbo.MachineSettings WHERE Id = 1;

    IF @oldId IS NULL
        INSERT dbo.MachineSettings(Id, MachineId, IpAddress, Port, AutoReconnect, UpdatedBy)
        VALUES (1, @MachineId, @IpAddress, @Port, @AutoReconnect, @User);
    ELSE
        UPDATE dbo.MachineSettings
        SET MachineId = @MachineId, IpAddress = @IpAddress, Port = @Port, AutoReconnect = @AutoReconnect,
            UpdatedAt = SYSDATETIME(), UpdatedBy = @User
        WHERE Id = 1;

    INSERT dbo.MachineSettingsLog(ChangedBy, Detail)
    VALUES (@User, LEFT(
        N'Machine ID ' + ISNULL(@oldId, N'-') + N' -> ' + @MachineId +
        N'; address ' + ISNULL(@oldIp, N'-') + N':' + ISNULL(CAST(@oldPort AS nvarchar(10)), N'-') + N' -> ' + @IpAddress + N':' + CAST(@Port AS nvarchar(10)) +
        N'; auto reconnect ' + ISNULL(CASE @oldAuto WHEN 1 THEN N'on' ELSE N'off' END, N'-') + N' -> ' + CASE @AutoReconnect WHEN 1 THEN N'on' ELSE N'off' END, 400));

    SELECT N'OK' AS Result;
END
GO

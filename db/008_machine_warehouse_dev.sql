/*
  CMC_Logiwa - the warehouse this machine serves (DEV)
  Settings > Machine gets a drop-down with the Logiwa warehouses (read through the web VM); the chosen one is saved here, next to the
  machine ID, IP and port. The app then downloads only that warehouse's orders and sends it with label requests, and it refuses to
  connect to the machine until one is chosen.
  Run with an admin login in SSMS, after 001..007. Safe to re-run (column added only if missing, procedures are CREATE OR ALTER).
  Nothing is deleted. The warehouse is empty after this script: the app asks for it at the next start.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'dbo.MachineSettings', N'WarehouseId') IS NULL
    ALTER TABLE dbo.MachineSettings ADD WarehouseId nvarchar(64) NULL, WarehouseCode nvarchar(50) NULL;
GO

/* The one row. */
CREATE OR ALTER PROCEDURE dbo.sp_MachineSettings_Get
AS
BEGIN
    SET NOCOUNT ON;
    SELECT MachineId, IpAddress, Port, AutoReconnect, WarehouseId, WarehouseCode, UpdatedAt, UpdatedBy
    FROM dbo.MachineSettings WHERE Id = 1;
END
GO

/* Saves the settings and logs what changed. Result: OK, or BAD_MACHINE_ID / BAD_IP / BAD_PORT / BAD_WAREHOUSE (nothing saved).
   The machine ID goes into every message as "<id>|TYPE|..." so it may not contain the pipe (the app also refuses control characters).
   The warehouse is required and must be a Logiwa identifier (a GUID); the code is only for display. */
CREATE OR ALTER PROCEDURE dbo.sp_MachineSettings_Save
    @MachineId     nvarchar(20),
    @IpAddress     nvarchar(100),
    @Port          int,
    @AutoReconnect bit,
    @User          nvarchar(50) = NULL,
    @WarehouseId   nvarchar(64) = NULL,
    @WarehouseCode nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @MachineId   = LTRIM(RTRIM(@MachineId));
    SET @IpAddress   = LTRIM(RTRIM(@IpAddress));
    SET @WarehouseId = LOWER(LTRIM(RTRIM(@WarehouseId)));
    SET @WarehouseCode = NULLIF(LTRIM(RTRIM(@WarehouseCode)), N'');

    IF @MachineId IS NULL OR @MachineId = N'' OR CHARINDEX(N'|', @MachineId) > 0
    BEGIN SELECT N'BAD_MACHINE_ID' AS Result; RETURN; END

    IF @IpAddress IS NULL OR @IpAddress = N''
    BEGIN SELECT N'BAD_IP' AS Result; RETURN; END

    IF @Port IS NULL OR @Port < 1 OR @Port > 65535
    BEGIN SELECT N'BAD_PORT' AS Result; RETURN; END

    IF @WarehouseId IS NULL OR TRY_CAST(@WarehouseId AS uniqueidentifier) IS NULL
    BEGIN SELECT N'BAD_WAREHOUSE' AS Result; RETURN; END

    DECLARE @oldId nvarchar(20), @oldIp nvarchar(100), @oldPort int, @oldAuto bit, @oldWh nvarchar(64), @oldWhCode nvarchar(50);
    SELECT @oldId = MachineId, @oldIp = IpAddress, @oldPort = Port, @oldAuto = AutoReconnect, @oldWh = WarehouseId, @oldWhCode = WarehouseCode
    FROM dbo.MachineSettings WHERE Id = 1;

    IF @oldId IS NULL
        INSERT dbo.MachineSettings(Id, MachineId, IpAddress, Port, AutoReconnect, WarehouseId, WarehouseCode, UpdatedBy)
        VALUES (1, @MachineId, @IpAddress, @Port, @AutoReconnect, @WarehouseId, @WarehouseCode, @User);
    ELSE
        UPDATE dbo.MachineSettings
        SET MachineId = @MachineId, IpAddress = @IpAddress, Port = @Port, AutoReconnect = @AutoReconnect,
            WarehouseId = @WarehouseId, WarehouseCode = @WarehouseCode,
            UpdatedAt = SYSDATETIME(), UpdatedBy = @User
        WHERE Id = 1;

    -- A different warehouse: download its orders from the start (orders already stored are only refreshed, never duplicated).
    IF @oldWh IS NOT NULL AND @oldWh <> @WarehouseId
        UPDATE dbo.SyncState SET LastSyncSeq = 0 WHERE Id = 1;

    INSERT dbo.MachineSettingsLog(ChangedBy, Detail)
    VALUES (@User, LEFT(
        N'Machine ID ' + ISNULL(@oldId, N'-') + N' -> ' + @MachineId +
        N'; address ' + ISNULL(@oldIp, N'-') + N':' + ISNULL(CAST(@oldPort AS nvarchar(10)), N'-') + N' -> ' + @IpAddress + N':' + CAST(@Port AS nvarchar(10)) +
        N'; auto reconnect ' + ISNULL(CASE @oldAuto WHEN 1 THEN N'on' ELSE N'off' END, N'-') + N' -> ' + CASE @AutoReconnect WHEN 1 THEN N'on' ELSE N'off' END +
        N'; warehouse ' + ISNULL(@oldWhCode, ISNULL(@oldWh, N'-')) + N' -> ' + ISNULL(@WarehouseCode, @WarehouseId), 400));

    SELECT N'OK' AS Result;
END
GO

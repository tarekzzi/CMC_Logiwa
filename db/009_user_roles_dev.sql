/*
  CMC_Logiwa - user roles (DEV)
  Users get an Administrator flag. Only administrators manage users, change the machine, database and web service settings,
  switch the environment and change the web VM / Logiwa settings. Operators log in, run the machine and change their own password.
  Run with an admin login in SSMS, after 001..008. Safe to re-run (column added only if missing, procedures are CREATE OR ALTER).
  Nothing is deleted. On the first run the oldest active user becomes administrator, so somebody can still manage the PC;
  give other people the role in Settings > Users.
*/
USE [Local_CMC_Logiwa_DEV];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'dbo.Users', N'IsAdmin') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD IsAdmin bit NOT NULL CONSTRAINT DF_Users_IsAdmin DEFAULT (0);
END
GO

/* Nobody is administrator yet (first run of this script): the oldest active user becomes one. */
IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE IsAdmin = 1)
    UPDATE dbo.Users SET IsAdmin = 1
    WHERE UserId = (SELECT TOP (1) UserId FROM dbo.Users WHERE IsActive = 1 ORDER BY CreatedAt, UserId);
GO

CREATE OR ALTER PROCEDURE dbo.sp_User_Get
    @Username nvarchar(50)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT UserId, Username, PasswordHash, Salt, Iterations, IsActive, IsAdmin
    FROM dbo.Users
    WHERE Username = @Username;
END
GO

/* The very first user is always an administrator (otherwise nobody could manage the PC). Result: OK or EXISTS. */
CREATE OR ALTER PROCEDURE dbo.sp_User_Create
    @Username     nvarchar(50),
    @PasswordHash varbinary(64),
    @Salt         varbinary(32),
    @Iterations   int,
    @IsAdmin      bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM dbo.Users WHERE Username = @Username)
    BEGIN
        SELECT CAST(0 AS int) AS UserId, N'EXISTS' AS Result;
        RETURN;
    END
    IF NOT EXISTS (SELECT 1 FROM dbo.Users) SET @IsAdmin = 1;
    INSERT dbo.Users(Username, PasswordHash, Salt, Iterations, IsAdmin) VALUES (@Username, @PasswordHash, @Salt, @Iterations, @IsAdmin);
    SELECT CAST(SCOPE_IDENTITY() AS int) AS UserId, N'OK' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_User_List
AS
BEGIN
    SET NOCOUNT ON;
    SELECT UserId, Username, IsActive, IsAdmin, CreatedAt, LastLoginAt FROM dbo.Users ORDER BY Username;
END
GO

/* Result: OK, NOT_FOUND, LAST_ACTIVE (nobody could log in) or LAST_ADMIN (nobody could manage the PC). */
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

    IF @IsActive = 0
       AND EXISTS (SELECT 1 FROM dbo.Users WHERE Username = @Username AND IsActive = 1 AND IsAdmin = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username <> @Username AND IsActive = 1 AND IsAdmin = 1)
    BEGIN
        SELECT N'LAST_ADMIN' AS Result;
        RETURN;
    END

    UPDATE dbo.Users SET IsActive = @IsActive WHERE Username = @Username;
    SELECT N'OK' AS Result;
END
GO

/* Gives or takes the administrator role. Result: OK, NOT_FOUND or LAST_ADMIN (the last active administrator keeps it). */
CREATE OR ALTER PROCEDURE dbo.sp_User_SetAdmin
    @Username nvarchar(50),
    @IsAdmin  bit
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username = @Username)
    BEGIN
        SELECT N'NOT_FOUND' AS Result;
        RETURN;
    END

    IF @IsAdmin = 0
       AND EXISTS (SELECT 1 FROM dbo.Users WHERE Username = @Username AND IsActive = 1 AND IsAdmin = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username <> @Username AND IsActive = 1 AND IsAdmin = 1)
    BEGIN
        SELECT N'LAST_ADMIN' AS Result;
        RETURN;
    END

    UPDATE dbo.Users SET IsAdmin = @IsAdmin WHERE Username = @Username;
    SELECT N'OK' AS Result;
END
GO

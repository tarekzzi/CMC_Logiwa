/*
  CMC_Logiwa - DEV application login
  Server: TAREK-ZAKI\TAREKSMART2025   Database: Local_CMC_Logiwa_DEV   Login: CMC_Logiwa_app_dev

  1. Run 001_schema_dev.sql first (it creates the database), then this script, then 002_procs_dev.sql.
  2. Run with an admin login (sysadmin) in SSMS or sqlcmd (-I).
  3. Replace the password below BEFORE running, and do not commit the edited file.
     (The script refuses to run while the placeholder is still there.)

  Permissions: read, write and EXECUTE on this one database only. No schema changes.
  PRO later: same script with Local_CMC_Logiwa_PRO and CMC_Logiwa_app_pro, on the production server.
*/
USE [master];
GO
DECLARE @pwd nvarchar(128) = N'CHANGE_ME_BEFORE_RUNNING';

IF @pwd = N'CHANGE_ME_BEFORE_RUNNING'
    THROW 50000, 'Edit the password in this script before running it.', 1;

IF SUSER_ID(N'CMC_Logiwa_app_dev') IS NULL
BEGIN
    DECLARE @sql nvarchar(max) =
        N'CREATE LOGIN [CMC_Logiwa_app_dev] WITH PASSWORD = N''' + REPLACE(@pwd, N'''', N'''''') +
        N''', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [Local_CMC_Logiwa_DEV];';
    EXEC (@sql);
    PRINT 'Login CMC_Logiwa_app_dev created.';
END
ELSE
    PRINT 'Login CMC_Logiwa_app_dev already exists (password not changed).';
GO

USE [Local_CMC_Logiwa_DEV];
GO
IF USER_ID(N'CMC_Logiwa_app_dev') IS NULL
    CREATE USER [CMC_Logiwa_app_dev] FOR LOGIN [CMC_Logiwa_app_dev] WITH DEFAULT_SCHEMA = [dbo];
GO
ALTER ROLE db_datareader ADD MEMBER [CMC_Logiwa_app_dev];
ALTER ROLE db_datawriter ADD MEMBER [CMC_Logiwa_app_dev];
GRANT EXECUTE ON SCHEMA::dbo TO [CMC_Logiwa_app_dev];
GO
PRINT 'CMC_Logiwa_app_dev: read, write, execute on Local_CMC_Logiwa_DEV.';
GO

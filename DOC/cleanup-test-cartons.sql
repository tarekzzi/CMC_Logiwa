/* Removes the test cartons created for the first CMC machine test (Local_CMC_Logiwa_DEV).
   Only rows whose ClientCode is 'TEST' are touched (plus their events and label requests).
   Run in SSMS with the database Local_CMC_Logiwa_DEV selected. */
USE [Local_CMC_Logiwa_DEV];
GO
DELETE e FROM dbo.PackJobEvent e WHERE e.OrderNumber IN (SELECT OrderNumber FROM dbo.PackJob WHERE ClientCode = N'TEST')
   OR e.OrderNumber IN (SELECT OrderNumber FROM dbo.PackJobArchive WHERE ClientCode = N'TEST') OR e.OrderNumber = N'NOSUCH99999';
DELETE FROM dbo.LabelRequest WHERE OrderNumber IN (SELECT OrderNumber FROM dbo.PackJob WHERE ClientCode = N'TEST');
DELETE FROM dbo.PackJobArchive WHERE ClientCode = N'TEST';
DELETE FROM dbo.PackJob WHERE ClientCode = N'TEST';
SELECT (SELECT COUNT(*) FROM dbo.PackJob) AS PackJobRowsLeft;
GO

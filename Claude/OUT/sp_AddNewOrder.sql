USE [CMC]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- Adds @UserName (logged-in WMS user) and stores it in PackJob.UserName.
-- Everything else is unchanged: MatchLab is still filled from @TrackingNumber (the @MatchLab parameter is ignored by the app).
ALTER PROCEDURE [dbo].[sp_AddNewOrder]
    @FulfilmentNumber NVARCHAR(20),
    @WaveNumber NVARCHAR(20),
    @SourceOrderNumber NVARCHAR(50),
    @TenantCode NVARCHAR(50),
    @TenantName NVARCHAR(250),
    @ItemCode NVARCHAR(250),
    @ItemBarcode NVARCHAR(250),
    @ItemSerialNo NVARCHAR(250),
    @Qty INT,
    @LabelDataLen NVARCHAR(50),
    @LabelData NVARCHAR(MAX),
    @MatchLab NVARCHAR(150),
    @TrackingNumber NVARCHAR(150),
    @UserName NVARCHAR(250) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [dbo].[PackJob]
    (
        [JobCreatedDateTime],
        [JobNumber],
        [OrderNumber],
        [SourceOrderNumber],
        [TenantCode],
        [TenantName],
        [ItemCode],
        [ItemBarcode],
        [ItemSerialNo],
        [Qty],
        [LabelDataLen],
        [LabelData],
        [MatchLab],
        [UserName]
    )
    VALUES
    (
        GETDATE(),
        @WaveNumber,
        @FulfilmentNumber,
        @SourceOrderNumber,
        @TenantCode,
        @TenantName,
        @ItemCode,
        @ItemBarcode,
        @ItemSerialNo,
        @Qty,
        TRY_CONVERT(INT, @LabelDataLen),
        @LabelData,
        @TrackingNumber,
        @UserName
    );

    SELECT SCOPE_IDENTITY() AS NewPackJobID;
END
GO

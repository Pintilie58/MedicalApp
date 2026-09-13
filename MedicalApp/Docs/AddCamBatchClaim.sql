BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260913150020_AddCamBatchClaim'
)
BEGIN
    DECLARE @var0 sysname;
    SELECT @var0 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ClinicBatchRuns]') AND [c].[name] = N'LeaseUntil');
    IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [ClinicBatchRuns] DROP CONSTRAINT [' + @var0 + '];');
    IF COL_LENGTH(N'[ClinicBatchRuns]', N'LeaseUntil') IS NOT NULL
        ALTER TABLE [ClinicBatchRuns] DROP COLUMN [LeaseUntil];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260913150020_AddCamBatchClaim'
)
BEGIN
    DECLARE @var1 sysname;
    SELECT @var1 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ClinicBatchRuns]') AND [c].[name] = N'RowVersion');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [ClinicBatchRuns] DROP CONSTRAINT [' + @var1 + '];');
    IF COL_LENGTH(N'[ClinicBatchRuns]', N'RowVersion') IS NOT NULL
        ALTER TABLE [ClinicBatchRuns] DROP COLUMN [RowVersion];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260913150020_AddCamBatchClaim'
)
BEGIN
    CREATE TABLE [ClinicBatchClaims] (
        [BatchRunId] int NOT NULL,
        [OwnerInstance] nvarchar(100) NOT NULL,
        [LeaseUntil] datetime2 NOT NULL,
        [ClaimedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ClinicBatchClaims] PRIMARY KEY ([BatchRunId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260913150020_AddCamBatchClaim'
)
BEGIN
    CREATE INDEX [IX_ClinicBatchClaims_LeaseUntil] ON [ClinicBatchClaims] ([LeaseUntil]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260913150020_AddCamBatchClaim'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260913150020_AddCamBatchClaim', N'9.0.0');
END;

COMMIT;
GO


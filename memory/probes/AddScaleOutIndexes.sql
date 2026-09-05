BEGIN TRANSACTION;
DROP INDEX [IX_Purchases_PurchasedAt] ON [Purchases];

DROP INDEX [IX_InterpretationHistories_User_Profile_Status] ON [InterpretationHistories];

DROP INDEX [IX_InterpretationHistories_UserEmail] ON [InterpretationHistories];

DROP INDEX [IX_ClinicAnalyses_ClinicId] ON [ClinicAnalyses];

DROP INDEX [IX_AiUsageLogs_CreatedAt] ON [AiUsageLogs];

DROP INDEX [IX_AiUsageLogs_Source] ON [AiUsageLogs];

DROP INDEX [IX_AiUsageLogs_Status] ON [AiUsageLogs];

CREATE INDEX [IX_Purchases_PurchasedAt] ON [Purchases] ([PurchasedAt]) INCLUDE ([AmountEur]);

CREATE INDEX [IX_InterpretationHistories_Profile_Status] ON [InterpretationHistories] ([ProfileId], [Status]);

CREATE INDEX [IX_InterpretationHistories_Status_Id_Desc] ON [InterpretationHistories] ([Status], [Id] DESC) INCLUDE ([DurationMs]);

CREATE INDEX [IX_InterpretationHistories_User_Id_Desc] ON [InterpretationHistories] ([UserEmail], [Id] DESC) INCLUDE ([Status]);

CREATE INDEX [IX_InterpretationHistories_User_Profile_Status] ON [InterpretationHistories] ([UserEmail], [ProfileId], [Status], [CreatedAt] DESC);

CREATE INDEX [IX_ClinicAnalyses_Clinic_ProcessedAt] ON [ClinicAnalyses] ([ClinicId], [ProcessedAt]) INCLUDE ([PatientId]);

CREATE INDEX [IX_AiUsageLogs_CreatedAt_Status] ON [AiUsageLogs] ([CreatedAt], [Status]) INCLUDE ([Source], [ModelUsed], [InputTokens], [OutputTokens]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260905190248_AddScaleOutIndexes', N'9.0.0');

COMMIT;
GO


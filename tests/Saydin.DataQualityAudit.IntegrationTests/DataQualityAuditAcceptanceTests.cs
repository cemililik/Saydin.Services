using System.Text.Json;
using FluentAssertions;
using Npgsql;

namespace Saydin.DataQualityAudit.IntegrationTests;

[Collection(AuditDatabaseCollection.Name)]
public sealed class DataQualityAuditAcceptanceTests(AuditDatabaseFixture database)
{
    [Theory]
    [InlineData("deny", "kms_sign_denied")]
    [InlineData("wrong_key", "kms_signature_response_invalid")]
    [InlineData("invalid_signature", "kms_signature_verification_failed")]
    [InlineData("timeout", "kms_sign_timeout")]
    public async Task ProductionKmsFailure_Returns6_AndPublishesNoBundle(
        string failure,
        string expectedCode)
    {
        var client = new FailingKmsClient(failure);

        var result = await database.RunKmsAuditAsync(client);

        result.ExitCode.Should().Be(AuditExitCodes.EvidenceFailure);
        result.Output.Should().BeEmpty();
        result.Error.Should().Contain($"code={expectedCode}");
        Directory.Exists(result.Bundle).Should().BeFalse();
        var stagingPattern = $".{Path.GetFileName(result.Bundle)}.staging-*";
        Directory.EnumerateFileSystemEntries(
            Path.GetDirectoryName(result.Bundle)!, stagingPattern).Should().BeEmpty();
        client.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("attribute", "backup_role_contract_drift")]
    [InlineData("membership", "backup_role_acl_or_membership_drift")]
    [InlineData("connect", "backup_role_acl_or_membership_drift")]
    [InlineData("app_select", "backup_role_acl_or_membership_drift")]
    public async Task BackupIdentityDrift_IsCriticalDq003_AndCleanupReturnsClean(
        string drift,
        string violationCode)
    {
        await database.SetBackupIdentityDriftAsync(drift, enabled: true);
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var dq3 = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003");
            dq3.Severity.Should().Be(AuditSeverity.Critical);
            dq3.Samples.Should().Contain(sample => sample.ViolationCode == violationCode);
        }
        finally
        {
            await database.SetBackupIdentityDriftAsync(drift, enabled: false);
        }

        var clean = await database.RunAuditAsync();
        clean.ExitCode.Should().Be(AuditExitCodes.Clean, clean.Error);
    }

    [Fact]
    public async Task CleanDatabase_Returns0_VerifiesSignature_AndContentHashIsIdempotent()
    {
        var manifest = await database.CreateManifestAsync();
        var first = await database.RunAuditAsync(manifest);
        var second = await database.RunAuditAsync(manifest);

        var firstContent = await ReadContentAsync(first.Bundle);
        var secondContent = await ReadContentAsync(second.Bundle);
        first.ExitCode.Should().Be(AuditExitCodes.Clean,
            $"{first.Error}; violations={ViolationSummary(firstContent)}");
        second.ExitCode.Should().Be(AuditExitCodes.Clean,
            $"{second.Error}; violations={ViolationSummary(secondContent)}");
        (await EvidenceBundle.VerifyAsync(first.Bundle, database.PublicKeyPath, default)).Should().BeTrue();
        var firstManifest = await ReadManifestAsync(first.Bundle);
        var secondManifest = await ReadManifestAsync(second.Bundle);
        firstManifest.KeyId.Should().Be(manifest.EvidenceKeyId)
            .And.NotBe(manifest.KeyId);
        firstManifest.ContentBundleSha256.Should().Be(secondManifest.ContentBundleSha256);
    }

    [Theory]
    [InlineData("coingecko")]
    [InlineData("tcmb")]
    [InlineData("openexchangerates")]
    [InlineData("twelvedata")]
    public async Task ForgedProviderEvidence_IsDq009_AndMalformedValuesDoNotAbort(string provider)
    {
        var anomaly = await database.CreateAuthorityEvidenceAnomalyAsync(provider);
        try
        {
            var result = await database.RunAuditAsync(anomaly.Manifest);
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var dq9 = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-009");
            dq9.Samples.Should().Contain(sample =>
                sample.ViolationCode == "normalized_evidence_mismatch");
            if (anomaly.Canary.Length > 0)
            {
                foreach (var file in Directory.EnumerateFiles(result.Bundle, "*", SearchOption.AllDirectories)
                             .Where(path => Path.GetExtension(path) != ".sig"))
                    (await File.ReadAllTextAsync(file)).Should().NotContain(anomaly.Canary);
            }
        }
        finally
        {
            await database.CleanupAuthorityEvidenceAnomalyAsync(anomaly);
        }
    }

    [Fact]
    public async Task MalformedEvdsEvidence_IsDq009_InsteadOfOperationalAbort()
    {
        var anomaly = await database.CreateInflationAuthorityAnomalyAsync();
        try
        {
            var result = await database.RunAuditAsync(anomaly.Manifest);
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-009").Samples
                .Should().Contain(sample => sample.ViolationCode == "normalized_evidence_mismatch");
        }
        finally
        {
            await database.CleanupInflationAuthorityAnomalyAsync(anomaly);
        }
    }

    [Fact]
    public async Task AllNullPriceAndTuikEvdsAuthority_AreHighDq009LegacyUnknown()
    {
        var anomaly = await database.CreateLegacyAuthorityAnomalyAsync();
        try
        {
            var result = await database.RunAuditAsync(anomaly.Manifest);
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var dq9 = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-009");
            dq9.Severity.Should().Be(AuditSeverity.High);
            dq9.TotalCount.Should().Be(2,
                "the disjoint legacy batches must count one scoped price and one TUİK/EVDS row");
            dq9.Samples.Count(sample => sample.ViolationCode == "legacy_authority_unknown")
                .Should().Be(2);
        }
        finally
        {
            await database.CleanupLegacyAuthorityAnomalyAsync(anomaly);
        }

        var clean = await database.RunAuditAsync();
        clean.ExitCode.Should().Be(AuditExitCodes.Clean, clean.Error);
    }

    [Theory]
    [InlineData("partial_authority_tuple", "partial_authority_tuple")]
    [InlineData("wrong_canonical_hash", "observation_hash_mismatch")]
    [InlineData("missing_attribution", "observation_attribution_missing")]
    [InlineData("orphan_fetch_payload", "orphan_fetch_payload")]
    [InlineData("forged_price_attribution", "forged_price_attribution")]
    [InlineData("forged_inflation_attribution", "forged_inflation_attribution")]
    public async Task AuthorityAndLedgerDataDrift_IsExactDq009_AndCleanupReturnsClean(
        string drift,
        string violationCode)
    {
        var fixture = await database.ApplyDq009DataDriftAsync(drift);
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-009").Samples
                .Should().Contain(sample => sample.ViolationCode == violationCode);
        }
        finally
        {
            await database.CleanupDq009DataDriftAsync(fixture);
        }

        var clean = await database.RunAuditAsync();
        clean.ExitCode.Should().Be(AuditExitCodes.Clean, clean.Error);
    }

    [Fact]
    public async Task DisabledPriceAuthorityTrigger_IsCriticalDq003StructureDrift()
    {
        await database.SetFetchPayloadLeaseTriggerEnabledAsync(false);
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var dq3 = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003");
            dq3.Severity.Should().Be(AuditSeverity.Critical);
            dq3.Samples.Should().Contain(sample =>
                sample.ViolationCode == "price_authority_structure_drift");
        }
        finally
        {
            await database.SetFetchPayloadLeaseTriggerEnabledAsync(true);
        }
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("wrong_overload")]
    [InlineData("public_execute")]
    public async Task PriceAuthorityFunctionIdentityBodyAndAclDrift_AreCriticalDq003(string drift)
    {
        var original = await database.ApplyAuthorityFunctionDriftAsync(drift);
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003").Samples
                .Should().Contain(sample =>
                    sample.ViolationCode == "price_authority_structure_drift");
        }
        finally
        {
            await database.RestoreAuthorityFunctionDriftAsync(drift, original);
        }
    }

    [Theory]
    [InlineData("wrong_relation")]
    [InlineData("wrong_kind")]
    [InlineData("replaced_pk")]
    [InlineData("fk_action")]
    [InlineData("foreign_table_grant")]
    [InlineData("column_grant")]
    public async Task PriceAuthorityConstraintIndexAndAclDrift_AreCriticalDq003(string drift)
    {
        await database.ApplyAuthorityStructureDriftAsync(drift);
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003").Samples
                .Should().Contain(sample =>
                    sample.ViolationCode == "price_authority_structure_drift");
        }
        finally
        {
            await database.RestoreAuthorityStructureDriftAsync(drift);
        }

        var clean = await database.RunAuditAsync();
        clean.ExitCode.Should().Be(AuditExitCodes.Clean, clean.Error);
    }

    [Theory]
    [InlineData("user_default")]
    [InlineData("raw_secret_column")]
    [InlineData("function_replacement")]
    [InlineData("wrong_overload")]
    [InlineData("public_execute")]
    [InlineData("trigger_disabled")]
    [InlineData("table_grant")]
    [InlineData("column_grant")]
    [InlineData("catalog_select_revoked")]
    public async Task ApiTrustSchemaFunctionTriggerAndAclDrift_AreCriticalDq003(
        string drift)
    {
        var originalDefinition = await database.ApplyApiTrustStructureDriftAsync(drift);
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003").Samples
                .Should().Contain(sample =>
                    sample.ViolationCode == "api_trust_structure_drift");

            if (drift == "raw_secret_column")
            {
                foreach (var file in Directory.EnumerateFiles(
                             result.Bundle, "*", SearchOption.AllDirectories)
                         .Where(path => Path.GetExtension(path) != ".sig"))
                    (await File.ReadAllTextAsync(file))
                        .Should().NotContain("TOP-SECRET-INSTALLATION-CANARY");
            }
        }
        finally
        {
            await database.RestoreApiTrustStructureDriftAsync(drift, originalDefinition);
        }

        var clean = await database.RunAuditAsync();
        clean.ExitCode.Should().Be(AuditExitCodes.Clean, clean.Error);
    }

    [Theory]
    [InlineData("function_replacement")]
    [InlineData("public_execute")]
    [InlineData("trigger_disabled")]
    [InlineData("foreign_key_action")]
    [InlineData("scheduler_acl_removed")]
    [InlineData("broad_owner_update")]
    [InlineData("compressed_chunk_acl")]
    public async Task PrincipalRetentionFunctionTriggerFkAndAclDrift_AreCriticalDq003(
        string drift)
    {
        var originalDefinition = await database.ApplyPrincipalRetentionStructureDriftAsync(drift);
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003").Samples
                .Should().Contain(sample =>
                    sample.ViolationCode == "principal_retention_structure_drift");
        }
        finally
        {
            await database.RestorePrincipalRetentionStructureDriftAsync(
                drift, originalDefinition);
        }

        var clean = await database.RunAuditAsync();
        clean.ExitCode.Should().Be(AuditExitCodes.Clean, clean.Error);
    }

    [Fact]
    public async Task MalformedAssetCatalogHash_IsFindingAndNeverOperationalAbort()
    {
        await database.ApplyMalformedAssetCatalogEvidenceAsync();
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003").Samples
                .Should().Contain(sample =>
                    sample.ViolationCode == "asset_catalog_state_drift");
        }
        finally
        {
            await database.RestoreAssetCatalogEvidenceAsync();
        }

        var clean = await database.RunAuditAsync();
        clean.ExitCode.Should().Be(AuditExitCodes.Clean, clean.Error);
    }

    [Fact]
    public async Task MissingObservation_AndInvalidPrice_Return2_WithNoRawBusinessKey()
    {
        await database.MakePriceGapAndInvalidAsync();
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var evidence = await File.ReadAllTextAsync(Path.Combine(result.Bundle, "evidence-content.json"));
            evidence.Should().Contain("missing_expected_observation")
                .And.Contain("nonpositive_close")
                .And.NotContain(database.AssetId.ToString("D"));
        }
        finally
        {
            await database.RestoreCleanPricesAsync();
        }
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("access_token")]
    [InlineData("client-secret")]
    [InlineData("credential")]
    public async Task SecretCanary_IsCountedButNeverEmitted(string providerKey)
    {
        const string canary = "TOP-SECRET-AUDIT-CANARY";
        await database.SetPriceRawAsync($"{{\"{providerKey}\":\"{canary}\"}}");
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var files = Directory.EnumerateFiles(result.Bundle, "*", SearchOption.AllDirectories);
            foreach (var file in files.Where(path => Path.GetExtension(path) != ".sig"))
                (await File.ReadAllTextAsync(file)).Should().NotContain(canary);
        }
        finally
        {
            await database.RestoreCleanPricesAsync();
        }
    }

    [Fact]
    public async Task WrongTargetAndBudgetReject_BeforeDataScan()
    {
        var wrongTarget = await database.CreateManifestAsync(database: "wrong_database");
        var targetResult = await database.RunAuditAsync(wrongTarget);
        targetResult.ExitCode.Should().Be(AuditExitCodes.PreflightRejected);
        Directory.Exists(targetResult.Bundle).Should().BeFalse();

        var tinyBudget = await database.CreateManifestAsync(maxDatabaseBytes: 1);
        var budgetResult = await database.RunAuditAsync(tinyBudget);
        budgetResult.ExitCode.Should().Be(AuditExitCodes.BudgetRejected);
        Directory.Exists(budgetResult.Bundle).Should().BeFalse();

        var relationBudget = await database.CreateManifestAsync(maxRelationBytes: 1);
        var relationResult = await database.RunAuditAsync(relationBudget);
        relationResult.ExitCode.Should().Be(AuditExitCodes.BudgetRejected);
        Directory.Exists(relationResult.Bundle).Should().BeFalse();
    }

    [Fact]
    public async Task DedicatedAuditRole_CannotWriteTruncateElevateOrDisableFence()
    {
        var access = await database.ReadAuditTablePrivilegeSummaryAsync();
        access.Should().Be((23, 18, 0));
        await using var connection = await database.OpenAuditConnectionAsync();
        foreach (var deniedTable in new[]
                 {
                     "activity_logs",
                     "market_holidays",
                     "saved_scenarios",
                     "users",
                     "installation_credentials",
                 })
        {
            await using var selectPrivilege = new NpgsqlCommand(
                "SELECT has_table_privilege(current_user, $1, 'SELECT')", connection);
            selectPrivilege.Parameters.AddWithValue($"public.{deniedTable}");
            Convert.ToBoolean(await selectPrivilege.ExecuteScalarAsync()).Should().BeFalse(
                $"the audit capability must not read {deniedTable}");
        }
        await using (var catalogPrivilege = new NpgsqlCommand(
                         "SELECT has_table_privilege(current_user, 'public.asset_catalog_state', 'SELECT')",
                         connection))
            Convert.ToBoolean(await catalogPrivilege.ExecuteScalarAsync()).Should().BeTrue();
        await using (var capability = new NpgsqlCommand("""
            SELECT NOT pg_has_role(current_user,'pg_monitor','MEMBER')
               AND (SELECT system_identifier IS NOT NULL FROM pg_control_system())
            """, connection))
            Convert.ToBoolean(await capability.ExecuteScalarAsync()).Should().BeTrue();
        foreach (var sql in new[]
                 {
                     "INSERT INTO public.price_points(asset_id,price_date,close) VALUES ('00000000-0000-0000-0000-000000000000','2024-01-01',1)",
                     "TRUNCATE public.ingestion_windows",
                     "ALTER TABLE public.price_points DISABLE TRIGGER trg_price_points_ingestion_fence",
                     "SET session_replication_role=replica",
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var action = async () => await command.ExecuteNonQueryAsync();
            var error = await action.Should().ThrowAsync<PostgresException>();
            error.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [Fact]
    public async Task WritablePrivilege_OnEachPreviouslyOmittedTable_FailsPreflight()
    {
        var grants = new[]
        {
            ("users", "INSERT"),
            ("saved_scenarios", "UPDATE"),
            ("activity_logs", "DELETE"),
            ("market_holidays", "TRUNCATE"),
            ("market_calendars", "INSERT"),
        };
        foreach (var (table, privilege) in grants)
        {
            await database.GrantAuditRolePrivilegeAsync(table, privilege, grant: true);
            try
            {
                var result = await database.RunAuditAsync();
                result.ExitCode.Should().Be(AuditExitCodes.PreflightRejected,
                    $"{privilege} on {table} must be fail-closed");
                Directory.Exists(result.Bundle).Should().BeFalse();
            }
            finally
            {
                await database.GrantAuditRolePrivilegeAsync(table, privilege, grant: false);
            }
        }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("permanent_failed")]
    public async Task FullRangeNonTerminalWindow_WithNoData_IsDq001NotFalseClean(string state)
    {
        await database.SetCleanLaneNonTerminalAsync(state);
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var content = await ReadContentAsync(result.Bundle);
            var completeness = content.Checks.Single(check => check.CheckId == "DQ-001");
            completeness.Samples.Should().Contain(sample =>
                sample.ViolationCode == $"window_not_terminal:{state}");
            content.Checks.Single(check => check.CheckId == "DQ-002").TotalCount.Should().Be(0);
        }
        finally
        {
            await database.RestoreCleanLaneAsync();
        }
    }

    [Fact]
    public async Task ShortOverlap_CannotRewindCursor_AndExactTrailingGapIsReported()
    {
        var manifest = await database.CreateOverlapShortLaneAsync();
        try
        {
            var result = await database.RunAuditAsync(manifest);
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var content = await ReadContentAsync(result.Bundle);
            var continuity = content.Checks.Single(check => check.CheckId == "DQ-002");
            continuity.TotalCount.Should().Be(2);
            continuity.Samples.Select(sample => sample.ViolationCode)
                .Should().BeEquivalentTo("overlapping_windows", "trailing_gap");
            var key = await File.ReadAllBytesAsync(database.HmacKeyPath);
            var expected = AuditCryptography.HmacBusinessKey(key,
                $"coingecko|{database.AssetId:D}|audit_overlap|1|2024-02-06|2024-02-10");
            continuity.Samples.Should().Contain(sample =>
                sample.ViolationCode == "trailing_gap" && sample.BusinessKeyHmac == expected);
        }
        finally
        {
            await database.CleanupOverlapShortLaneAsync();
        }
    }

    [Fact]
    public async Task WrongTypeFenceTrigger_AllowsLegacyInsertButDq003RejectsFingerprint()
    {
        await database.ApplyWrongTypePriceFenceAndBypassInsertAsync();
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var dq3 = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003");
            dq3.Samples.Should().Contain(sample => sample.ViolationCode == "price_fence_trigger_drift");
        }
        finally
        {
            await database.RestoreWriterFencesAsync();
        }
    }

    [Fact]
    public async Task SameNameFenceFunctionWithChangedBody_IsDq003()
    {
        await database.ApplyPriceFenceBodyDriftAsync();
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var dq3 = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003");
            dq3.Samples.Should().Contain(sample => sample.ViolationCode == "price_fence_trigger_drift");
        }
        finally
        {
            await database.RestoreWriterFencesAsync();
        }
    }

    [Fact]
    public async Task SameNameUniqueOnWrongAttnums_WithDuplicateLogicalRows_IsDq003()
    {
        await database.ApplyWindowUniqueFingerprintDriftAndDuplicatesAsync();
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var dq3 = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-003");
            dq3.Samples.Select(sample => sample.ViolationCode)
                .Should().Contain("window_logical_unique_drift")
                .And.Contain("duplicate_logical_window");
        }
        finally
        {
            await database.RestoreWindowUniqueConstraintAsync();
        }
    }

    [Fact]
    public async Task TerminalWindowWithOlderRunningJob_IsDq007EvenWhenLatestJobSucceeded()
    {
        await database.AddOlderRunningJobToSucceededWindowAsync();
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var dq7 = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-007");
            dq7.Samples.Should().Contain(sample =>
                sample.ViolationCode == "terminal_window_has_running_job");
        }
        finally
        {
            await database.RemoveOlderRunningJobAsync();
        }
    }

    [Fact]
    public async Task MissingActiveCalendarPointerAndEligibleBinding_AreDq006()
    {
        await database.RemoveCalendarPointerAndEligibleBindingAsync();
        try
        {
            var result = await database.RunAuditAsync();
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var codes = (await ReadContentAsync(result.Bundle)).Checks
                .Single(check => check.CheckId == "DQ-006").Samples
                .Select(sample => sample.ViolationCode);
            codes.Should().Contain("active_calendar_release_pointer_missing")
                .And.Contain("eligible_asset_calendar_binding_missing");
        }
        finally
        {
            await database.RestoreCalendarPointerAndEligibleBindingAsync();
        }
    }

    [Fact]
    public async Task ContainingDailyWindow_InnerSignedScopeIsCleanWithoutOutsideEvidence()
    {
        var manifest = await database.CreateManifestAsync();
        manifest = manifest with
        {
            Scope = manifest.Scope with
            {
                Lanes = [manifest.Scope.Lanes[0] with { From = database.Through }],
            },
        };

        var result = await database.RunAuditAsync(manifest);

        result.ExitCode.Should().Be(AuditExitCodes.Clean, result.Error);
        var content = await ReadContentAsync(result.Bundle);
        content.Checks.Single(check => check.CheckId == "DQ-001").TotalCount.Should().Be(0);
        content.Checks.Single(check => check.CheckId == "DQ-002").TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task ContainingMonthlyWindow_CompletenessIsCleanButLegacyAuthorityIsReported()
    {
        var manifest = await database.CreateContainingMonthlyLaneAsync();
        try
        {
            var result = await database.RunAuditAsync(manifest);
            var content = await ReadContentAsync(result.Bundle);
            result.ExitCode.Should().Be(AuditExitCodes.Violations,
                $"{result.Error}; violations={string.Join(',', content.Checks.Where(check => check.TotalCount > 0).SelectMany(check => check.Samples.Select(sample => check.CheckId + ':' + sample.ViolationCode)))}");
            content.Checks.Single(check => check.CheckId == "DQ-001").TotalCount.Should().Be(0);
            content.Checks.Single(check => check.CheckId == "DQ-002").TotalCount.Should().Be(0);
            var authority = content.Checks.Single(check => check.CheckId == "DQ-009");
            authority.TotalCount.Should().Be(1);
            authority.Samples.Should().ContainSingle(sample =>
                sample.ViolationCode == "legacy_authority_unknown");
        }
        finally
        {
            await database.CleanupContainingMonthlyLaneAsync();
        }
    }

    [Fact]
    public async Task CancelledRun_Returns5_WithoutBundle()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await database.RunAuditAsync(cancellationToken: cancellation.Token);

        result.ExitCode.Should().Be(AuditExitCodes.RuntimeFailure);
        Directory.Exists(result.Bundle).Should().BeFalse();
    }

    [Fact]
    public async Task PostgreSqlLockTimeout_Returns5_WithoutMisclassifyingDataViolation()
    {
        await using var admin = await database.OpenAdminConnectionAsync();
        await using var transaction = await admin.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
                         "LOCK TABLE public.schema_migrations IN ACCESS EXCLUSIVE MODE", admin, transaction))
            await command.ExecuteNonQueryAsync();

        var result = await database.RunAuditAsync();

        result.ExitCode.Should().Be(AuditExitCodes.RuntimeFailure);
        result.Error.Should().Contain("code=postgres_55P03");
        Directory.Exists(result.Bundle).Should().BeFalse();
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task EvidenceTamperOrExtraFile_Returns6()
    {
        var result = await database.RunAuditAsync();
        result.ExitCode.Should().Be(AuditExitCodes.Clean, result.Error);
        await File.WriteAllTextAsync(Path.Combine(result.Bundle, "extra.csv"), "unexpected");
        var error = new StringWriter();

        var exit = await AuditApplication.RunAsync([
            "verify-evidence", "--bundle", result.Bundle, "--public-key", database.PublicKeyPath,
        ], TextWriter.Null, error, TimeProvider.System);

        exit.Should().Be(AuditExitCodes.EvidenceFailure);
    }

    [Fact]
    public async Task BoundedAnomalyMatrix_DetectsDq002ThroughDq008_ThenCleanupReturns0()
    {
        var manifest = await database.CreateAnomalyMatrixAsync();
        try
        {
            var result = await database.RunAuditAsync(manifest);
            result.ExitCode.Should().Be(AuditExitCodes.Violations, result.Error);
            var evidence = await File.ReadAllTextAsync(
                Path.Combine(result.Bundle, "evidence-content.json"));
            foreach (var expected in new[]
                     {
                         "trailing_gap",
                         "price_fence_trigger_drift",
                         "nonpositive_cpi",
                         "seed_without_tuik",
                         "window_calendar_release_scope_or_coverage_mismatch",
                         "expired_running_lease",
                         "job_window_scope_mismatch",
                         "post_fence_price_without_succeeded_window",
                         "post_grace_job_without_window",
                     })
                evidence.Should().Contain(expected);
        }
        finally
        {
            await database.CleanupAnomalyMatrixAsync();
        }

        var clean = await database.RunAuditAsync();
        clean.ExitCode.Should().Be(AuditExitCodes.Clean, clean.Error);
    }

    private static async Task<EvidenceManifest> ReadManifestAsync(string bundle)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(bundle, "manifest.json"));
        return JsonSerializer.Deserialize(bytes, AuditJsonContext.Default.EvidenceManifest)!;
    }

    private static async Task<EvidenceContent> ReadContentAsync(string bundle)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(bundle, "evidence-content.json"));
        return JsonSerializer.Deserialize(bytes, AuditJsonContext.Default.EvidenceContent)!;
    }

    private static string ViolationSummary(EvidenceContent content) => string.Join(',',
        content.Checks.Where(check => check.TotalCount > 0).SelectMany(check =>
            check.Samples.Select(sample => $"{check.CheckId}:{sample.ViolationCode}")));

    private sealed class FailingKmsClient(string failure) : IOciKmsSigningClient
    {
        public bool Disposed { get; private set; }

        public async Task<OciKmsSignatureResponse> SignDigestAsync(
            string keyId,
            string keyVersionId,
            ReadOnlyMemory<byte> sha256Digest,
            CancellationToken cancellationToken)
        {
            switch (failure)
            {
                case "deny":
                    throw new UnauthorizedAccessException();
                case "wrong_key":
                    return new OciKmsSignatureResponse(
                        keyId + "-wrong", keyVersionId, "EcdsaSha256",
                        Convert.ToBase64String(new byte[64]));
                case "invalid_signature":
                    using (var wrongKey = System.Security.Cryptography.ECDsa.Create(
                               System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
                    {
                        var signature = wrongKey.SignHash(
                            sha256Digest.Span,
                            System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
                        return new OciKmsSignatureResponse(
                            keyId, keyVersionId, "EcdsaSha256",
                            Convert.ToBase64String(signature));
                    }
                case "timeout":
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                default:
                    throw new ArgumentOutOfRangeException(nameof(failure));
            }
        }

        public void Dispose() => Disposed = true;
    }
}

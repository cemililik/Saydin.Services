using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Saydin.DatabaseMigrator;

internal enum MigrationExecutionMode
{
    Transactional,
    ResumableOnline,
}

internal static class SqlImpactKinds
{
    public const string TransactionalDdl = "transactional-ddl";
    public const string TableRewrite = "table-rewrite";
    public const string ValidateConstraint = "validate-constraint";
    public const string CreateIndexNonConcurrent = "create-index-nonconcurrent";
    public const string CreateIndexConcurrent = "create-index-concurrent";
    public const string LargeDml = "large-dml";
    public const string TimescaleCompression = "timescale-compression";
    public const string TimescaleChunkOperation = "timescale-chunk-operation";
    public const string OpaqueOrUnknown = "opaque-or-unknown";
    public const string ResumableOnline = "resumable-online";

    public static IReadOnlySet<string> Heavy { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TableRewrite,
        ValidateConstraint,
        CreateIndexNonConcurrent,
        CreateIndexConcurrent,
        LargeDml,
        TimescaleCompression,
        TimescaleChunkOperation,
    };
}

internal sealed record MigrationImpactTarget(
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("requiredPredecessorSha256")] string RequiredPredecessorSha256,
    [property: JsonPropertyName("requiredPredecessorVersion")] string RequiredPredecessorVersion,
    [property: JsonPropertyName("requiredSchemaManifestSha256")] string RequiredSchemaManifestSha256,
    [property: JsonPropertyName("systemIdentifierSha256")] string SystemIdentifierSha256);

internal sealed record MigrationImpactBudgets(
    [property: JsonPropertyName("declaredTablespaceCapacityBytes")] long DeclaredTablespaceCapacityBytes,
    [property: JsonPropertyName("estimatedAdditionalBytes")] long EstimatedAdditionalBytes,
    [property: JsonPropertyName("lockTimeoutMilliseconds")] int LockTimeoutMilliseconds,
    [property: JsonPropertyName("maxBlockingTransactionAgeSeconds")] int MaxBlockingTransactionAgeSeconds,
    [property: JsonPropertyName("maxCompressedBytes")] long MaxCompressedBytes,
    [property: JsonPropertyName("maxProjectedWalBytes")] long MaxProjectedWalBytes,
    [property: JsonPropertyName("maxRelationBytes")] long MaxRelationBytes,
    [property: JsonPropertyName("maxReplicaLagBytes")] long MaxReplicaLagBytes,
    [property: JsonPropertyName("maxSlotRetentionBytes")] long MaxSlotRetentionBytes,
    [property: JsonPropertyName("maxWaitingLocks")] int MaxWaitingLocks,
    [property: JsonPropertyName("minFreeBytesAfter")] long MinFreeBytesAfter,
    [property: JsonPropertyName("minHeadroomRatioBasisPoints")] int MinHeadroomRatioBasisPoints,
    [property: JsonPropertyName("minimumStreamingReplicas")] int MinimumStreamingReplicas,
    [property: JsonPropertyName("requireAllSlotsActive")] bool RequireAllSlotsActive,
    [property: JsonPropertyName("statementTimeoutMilliseconds")] int StatementTimeoutMilliseconds,
    [property: JsonPropertyName("totalTimeoutSeconds")] int TotalTimeoutSeconds);

internal sealed record MigrationImpactRelation(
    [property: JsonPropertyName("includeChunks")] bool IncludeChunks,
    [property: JsonPropertyName("includeCompressed")] bool IncludeCompressed,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tablespace")] string Tablespace);

internal sealed record MigrationImpactPostcondition(
    [property: JsonPropertyName("column")] string? Column,
    [property: JsonPropertyName("index")] string? Index,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("relation")] string Relation);

internal sealed record MigrationOnlinePlan(
    [property: JsonPropertyName("batchSize")] int BatchSize,
    [property: JsonPropertyName("keyColumn")] string KeyColumn,
    [property: JsonPropertyName("maxBatchMilliseconds")] int MaxBatchMilliseconds,
    [property: JsonPropertyName("pauseCompressionPolicy")] bool PauseCompressionPolicy,
    [property: JsonPropertyName("planKind")] string PlanKind,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("targetColumn")] string TargetColumn,
    [property: JsonPropertyName("targetType")] string TargetType,
    [property: JsonPropertyName("targetValue")] JsonElement TargetValue);

internal sealed record MigrationImpactDocument(
    [property: JsonPropertyName("budgets")] MigrationImpactBudgets Budgets,
    [property: JsonPropertyName("classifications")] string[] Classifications,
    [property: JsonPropertyName("executionMode")] string ExecutionMode,
    [property: JsonPropertyName("migrationSha256")] string MigrationSha256,
    [property: JsonPropertyName("migrationVersion")] string MigrationVersion,
    [property: JsonPropertyName("onlinePlan")] MigrationOnlinePlan? OnlinePlan,
    [property: JsonPropertyName("postconditions")] MigrationImpactPostcondition[] Postconditions,
    [property: JsonPropertyName("relations")] MigrationImpactRelation[] Relations,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("target")] MigrationImpactTarget Target);

internal sealed record MigrationImpactDefinition(
    MigrationImpactDocument Document,
    MigrationExecutionMode Mode,
    string ManifestSha256,
    byte[] CanonicalBytes,
    SqlImpactAnalysis SqlAnalysis);

internal sealed record MigrationImpactConfiguration(
    string Directory,
    string PublicKeyFile,
    string PublicKeySha256);

internal sealed record SqlImpactAnalysis(
    IReadOnlyList<string> Classifications,
    IReadOnlyList<string> Relations,
    int StatementCount);

internal sealed class MigrationImpactSet
{
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumSignatureBytes = 1_024;
    private const int MaximumPublicKeyBytes = 16 * 1024;
    private static readonly Regex VersionPattern = new(
        "^[0-9]{3}[a-z]?_[a-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RelationPattern = new(
        "^public\\.[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IdentifierPattern = new(
        "^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IReadOnlyDictionary<string, MigrationImpactDefinition> definitions;

    private MigrationImpactSet(IReadOnlyDictionary<string, MigrationImpactDefinition> definitions) =>
        this.definitions = definitions;

    public MigrationImpactDefinition For(string version) =>
        definitions.TryGetValue(version, out var definition)
            ? definition
            : throw new MigratorRejectedException("migration_impact_manifest_missing", version);

    public static MigrationImpactSet Empty { get; } =
        new(new Dictionary<string, MigrationImpactDefinition>(StringComparer.Ordinal));

    public static MigrationImpactSet LoadAndVerify(
        MigrationManifest manifest,
        int trustedPrefixCount,
        MigrationImpactConfiguration? configuration)
    {
        var tail = manifest.Migrations.Skip(trustedPrefixCount).ToArray();
        if (tail.Length == 0) return Empty;
        if (configuration is null)
            throw new MigratorRejectedException("migration_impact_configuration_required");
        if (!Directory.Exists(configuration.Directory) ||
            !Path.IsPathFullyQualified(configuration.Directory) ||
            !Path.IsPathFullyQualified(configuration.PublicKeyFile) ||
            !Sha256Pattern.IsMatch(configuration.PublicKeySha256))
            throw new MigratorRejectedException("migration_impact_configuration_invalid");

        using var publicKey = LoadPublicKey(
            configuration.PublicKeyFile, configuration.PublicKeySha256);
        var result = new Dictionary<string, MigrationImpactDefinition>(StringComparer.Ordinal);
        foreach (var migration in tail)
        {
            var manifestPath = Path.Combine(
                configuration.Directory, $"{migration.Version}.impact.json");
            var signaturePath = Path.Combine(
                configuration.Directory, $"{migration.Version}.impact.sig");
            var raw = ReadBoundedRegularFile(
                manifestPath, MaximumManifestBytes, "migration_impact_manifest_unreadable");
            var canonical = CanonicalJson.Canonicalize(raw);
            if (!raw.AsSpan().SequenceEqual(canonical))
                throw new MigratorRejectedException(
                    "migration_impact_manifest_not_canonical", migration.Version);
            var signatureText = Encoding.ASCII.GetString(ReadBoundedRegularFile(
                signaturePath, MaximumSignatureBytes, "migration_impact_signature_unreadable"));
            if (signatureText.Length == 0 || signatureText != signatureText.Trim() ||
                signatureText.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new MigratorRejectedException(
                    "migration_impact_signature_invalid", migration.Version);
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(signatureText);
            }
            catch (FormatException exception)
            {
                throw new MigratorRejectedException(
                    "migration_impact_signature_invalid", migration.Version, exception);
            }
            if (!string.Equals(Convert.ToBase64String(signature), signatureText, StringComparison.Ordinal) ||
                !publicKey.VerifyData(canonical, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                throw new MigratorRejectedException(
                    "migration_impact_signature_invalid", migration.Version);

            MigrationImpactDocument document;
            try
            {
                document = JsonSerializer.Deserialize<MigrationImpactDocument>(
                    canonical, SerializerOptions) ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw new MigratorRejectedException(
                    "migration_impact_manifest_invalid", migration.Version, exception);
            }
            var analysis = SqlImpactAnalyzer.Analyze(migration.ReadSql());
            var definition = Validate(document, migration, manifest, analysis, canonical);
            if (!result.TryAdd(migration.Version, definition))
                throw new MigratorRejectedException(
                    "migration_impact_manifest_duplicate", migration.Version);
        }

        var expectedFiles = tail.SelectMany(migration => new[]
            {
                $"{migration.Version}.impact.json", $"{migration.Version}.impact.sig",
            })
            .Order(StringComparer.Ordinal).ToArray();
        var actualFiles = Directory.EnumerateFiles(configuration.Directory)
            .Select(path => new FileInfo(path).Name)
            .Where(name => name.EndsWith(".impact.json", StringComparison.Ordinal) ||
                           name.EndsWith(".impact.sig", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (!actualFiles.SequenceEqual(expectedFiles, StringComparer.Ordinal))
            throw new MigratorRejectedException("migration_impact_file_set_mismatch");
        return new MigrationImpactSet(result);
    }

    private static MigrationImpactDefinition Validate(
        MigrationImpactDocument document,
        MigrationDefinition migration,
        MigrationManifest manifest,
        SqlImpactAnalysis analysis,
        byte[] canonical)
    {
        if (document.SchemaVersion != 1 || !VersionPattern.IsMatch(document.MigrationVersion) ||
            !string.Equals(document.MigrationVersion, migration.Version, StringComparison.Ordinal) ||
            !Sha256Pattern.IsMatch(document.MigrationSha256) ||
            !CryptographicEquals(document.MigrationSha256, migration.Checksum))
            throw new MigratorRejectedException("migration_impact_identity_mismatch", migration.Version);
        var migrationIndex = -1;
        for (var index = 0; index < manifest.Migrations.Count; index++)
            if (ReferenceEquals(manifest.Migrations[index], migration))
            {
                migrationIndex = index;
                break;
            }
        if (migrationIndex <= 0)
            throw new MigratorRejectedException("migration_impact_predecessor_invalid", migration.Version);
        var predecessor = manifest.Migrations[migrationIndex - 1];
        var target = document.Target;
        if (target is null || !string.Equals(target.RequiredPredecessorVersion,
                predecessor.Version, StringComparison.Ordinal) ||
            !CryptographicEquals(target.RequiredPredecessorSha256, predecessor.Checksum) ||
            !CryptographicEquals(target.RequiredSchemaManifestSha256,
                manifest.ChecksumThrough(migrationIndex)) ||
            !Sha256Pattern.IsMatch(target.SystemIdentifierSha256) ||
            string.IsNullOrWhiteSpace(target.Database))
            throw new MigratorRejectedException("migration_impact_predecessor_invalid", migration.Version);

        ValidateBudgets(document.Budgets, migration.Version);
        var relations = document.Relations ?? [];
        if (relations.Length != relations.Select(relation => relation.Name)
                .Distinct(StringComparer.Ordinal).Count() ||
            relations.Any(relation => !RelationPattern.IsMatch(relation.Name) ||
                                      relation.Tablespace is not "pg_default" &&
                                      !IdentifierPattern.IsMatch(relation.Tablespace)) ||
            !relations.Select(relation => relation.Name)
                .SequenceEqual(relations.Select(relation => relation.Name)
                    .Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new MigratorRejectedException("migration_impact_relation_invalid", migration.Version);

        var mode = document.ExecutionMode switch
        {
            "transactional" => MigrationExecutionMode.Transactional,
            "resumable-online" => MigrationExecutionMode.ResumableOnline,
            _ => throw new MigratorRejectedException(
                "migration_impact_execution_mode_invalid", migration.Version),
        };
        var declaredClassifications = document.Classifications ?? [];
        if (declaredClassifications.Length == 0 ||
            declaredClassifications.Distinct(StringComparer.Ordinal).Count() !=
            declaredClassifications.Length ||
            !declaredClassifications.SequenceEqual(
                declaredClassifications.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new MigratorRejectedException(
                "migration_impact_classification_invalid", migration.Version);
        if (mode == MigrationExecutionMode.Transactional)
        {
            if (analysis.StatementCount == 0 || analysis.Classifications.Contains(
                    SqlImpactKinds.OpaqueOrUnknown, StringComparer.Ordinal) ||
                analysis.Classifications.Contains(
                    SqlImpactKinds.CreateIndexConcurrent, StringComparer.Ordinal) ||
                !declaredClassifications.SequenceEqual(
                    analysis.Classifications, StringComparer.Ordinal) ||
                !relations.Select(relation => relation.Name).SequenceEqual(
                    analysis.Relations, StringComparer.Ordinal) ||
                document.OnlinePlan is not null || document.Postconditions is null ||
                document.Postconditions.Length == 0)
                throw new MigratorRejectedException(
                    "migration_impact_static_classification_mismatch", migration.Version);
        }
        else
        {
            if (analysis.StatementCount != 0 ||
                declaredClassifications is not [SqlImpactKinds.ResumableOnline] ||
                document.OnlinePlan is null || document.Postconditions is null)
                throw new MigratorRejectedException(
                    "migration_online_plan_contract_invalid", migration.Version);
            ValidateOnlinePlan(document.OnlinePlan, relations, migration.Version);
        }
        ValidatePostconditions(document.Postconditions, relations, migration.Version);
        return new MigrationImpactDefinition(
            document,
            mode,
            Convert.ToHexStringLower(SHA256.HashData(canonical)),
            canonical,
            analysis);
    }

    private static void ValidateBudgets(MigrationImpactBudgets? budgets, string version)
    {
        if (budgets is null || budgets.DeclaredTablespaceCapacityBytes <= 0 ||
            budgets.EstimatedAdditionalBytes < 0 || budgets.MaxRelationBytes <= 0 ||
            budgets.MaxCompressedBytes < 0 || budgets.MaxProjectedWalBytes < 0 ||
            budgets.MaxReplicaLagBytes < 0 || budgets.MaxSlotRetentionBytes < 0 ||
            budgets.MinFreeBytesAfter < 0 || budgets.MinHeadroomRatioBasisPoints is < 0 or > 10_000 ||
            budgets.MaxWaitingLocks is < 0 or > 100 ||
            budgets.MaxBlockingTransactionAgeSeconds is < 1 or > 3_600 ||
            budgets.MinimumStreamingReplicas is < 0 or > 32 ||
            budgets.LockTimeoutMilliseconds is < 1 or > 300_000 ||
            budgets.StatementTimeoutMilliseconds is < 1 or > 3_600_000 ||
            budgets.TotalTimeoutSeconds is < 5 or > 7_200 ||
            budgets.EstimatedAdditionalBytes > budgets.MaxProjectedWalBytes)
            throw new MigratorRejectedException("migration_impact_budget_invalid", version);
    }

    private static void ValidateOnlinePlan(
        MigrationOnlinePlan plan,
        MigrationImpactRelation[] relations,
        string version)
    {
        if (plan.PlanKind != "uuid-keyset-set-constant-where-null" ||
            !RelationPattern.IsMatch(plan.Relation) ||
            relations.Length != 1 || relations[0].Name != plan.Relation ||
            !IdentifierPattern.IsMatch(plan.KeyColumn) ||
            !IdentifierPattern.IsMatch(plan.TargetColumn) ||
            plan.KeyColumn == plan.TargetColumn || plan.BatchSize is < 1 or > 10_000 ||
            plan.MaxBatchMilliseconds is < 10 or > 300_000 ||
            plan.TargetType is not ("boolean" or "smallint" or "integer" or "bigint" or
                "text" or "uuid") || plan.TargetValue.ValueKind is JsonValueKind.Null or
                JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined ||
            plan.PauseCompressionPolicy && !relations[0].IncludeChunks)
            throw new MigratorRejectedException("migration_online_plan_contract_invalid", version);
    }

    private static void ValidatePostconditions(
        MigrationImpactPostcondition[] postconditions,
        MigrationImpactRelation[] relations,
        string version)
    {
        var relationNames = relations.Select(relation => relation.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var postcondition in postconditions)
        {
            if (!relationNames.Contains(postcondition.Relation) ||
                postcondition.Kind switch
                {
                    "column-no-null" => !IdentifierPattern.IsMatch(postcondition.Column ?? string.Empty) ||
                                        postcondition.Index is not null,
                    "index-valid" => !IdentifierPattern.IsMatch(postcondition.Index ?? string.Empty) ||
                                     postcondition.Column is not null,
                    "relation-exists" => postcondition.Column is not null || postcondition.Index is not null,
                    _ => true,
                })
                throw new MigratorRejectedException("migration_impact_postcondition_invalid", version);
        }
    }

    private static ECDsa LoadPublicKey(string path, string expectedSha256)
    {
        var bytes = ReadBoundedRegularFile(
            path, MaximumPublicKeyBytes, "migration_impact_public_key_unreadable");
        var pem = Encoding.UTF8.GetString(bytes);
        if (!pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ||
            pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new MigratorRejectedException("migration_impact_public_key_invalid");
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            var parameters = key.ExportParameters(false);
            if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                parameters.Q.X?.Length != 32 || parameters.Q.Y?.Length != 32)
                throw new CryptographicException();
            var actual = Convert.ToHexStringLower(SHA256.HashData(
                key.ExportSubjectPublicKeyInfo()));
            if (!CryptographicEquals(actual, expectedSha256))
                throw new MigratorRejectedException("migration_impact_public_key_mismatch");
            return key;
        }
        catch (MigratorRejectedException)
        {
            key.Dispose();
            throw;
        }
        catch (CryptographicException exception)
        {
            key.Dispose();
            throw new MigratorRejectedException(
                "migration_impact_public_key_invalid", innerException: exception);
        }
    }

    private static byte[] ReadBoundedRegularFile(string path, int maximumBytes, string code)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length is <= 0 ||
                info.Length > maximumBytes)
                throw new MigratorRejectedException(code);
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != info.Length || bytes.Length > maximumBytes)
                throw new MigratorRejectedException(code);
            return bytes;
        }
        catch (MigratorRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MigratorRejectedException(code, innerException: exception);
        }
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

internal static class CanonicalJson
{
    public static byte[] Canonicalize(byte[] input)
    {
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            RejectDuplicateProperties(document.RootElement);
            using var stream = new MemoryStream(input.Length);
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false,
                   }))
                WriteCanonical(writer, document.RootElement);
            return stream.ToArray();
        }
        catch (JsonException exception)
        {
            throw new MigratorRejectedException(
                "migration_impact_manifest_invalid", innerException: exception);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException("duplicate property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var number)) throw new JsonException("integer required");
                writer.WriteNumberValue(number);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("unsupported value");
        }
    }
}

internal static class SqlImpactAnalyzer
{
    private static readonly Regex TokenPattern = new(
        "[a-z_][a-z0-9_$]*|[0-9]+|[.;(),]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static SqlImpactAnalysis Analyze(string sql)
    {
        var sanitized = Sanitize(sql);
        var statements = sanitized.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(statement => TokenPattern.Matches(statement)
                .Select(match => match.Value).ToArray())
            .Where(tokens => tokens.Length > 0).ToArray();
        var classifications = new HashSet<string>(StringComparer.Ordinal);
        var relations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tokens in statements) AnalyzeStatement(tokens, classifications, relations);
        return new SqlImpactAnalysis(
            classifications.Order(StringComparer.Ordinal).ToArray(),
            relations.Order(StringComparer.Ordinal).ToArray(),
            statements.Length);
    }

    private static void AnalyzeStatement(
        string[] tokens,
        ISet<string> classifications,
        ISet<string> relations)
    {
        var first = tokens[0];
        if (first == "alter" && tokens.ElementAtOrDefault(1) == "table")
        {
            AddRelation(tokens, 2, relations, classifications);
            if (ContainsSequence(tokens, "validate", "constraint"))
                classifications.Add(SqlImpactKinds.ValidateConstraint);
            if (ContainsSequence(tokens, "alter", "column") &&
                    (tokens.Contains("type", StringComparer.Ordinal) ||
                     ContainsSequence(tokens, "set", "data", "type")) ||
                ContainsSequence(tokens, "set", "not", "null") ||
                ContainsSequence(tokens, "add", "column") && tokens.Contains("default", StringComparer.Ordinal) ||
                ContainsSequence(tokens, "add", "constraint") &&
                    !ContainsSequence(tokens, "not", "valid"))
                classifications.Add(SqlImpactKinds.TableRewrite);
            if (!classifications.Contains(SqlImpactKinds.TableRewrite) &&
                !classifications.Contains(SqlImpactKinds.ValidateConstraint))
                classifications.Add(SqlImpactKinds.TransactionalDdl);
            return;
        }
        if (first == "create" &&
            (tokens.ElementAtOrDefault(1) == "index" ||
             tokens.ElementAtOrDefault(1) == "unique" && tokens.ElementAtOrDefault(2) == "index"))
        {
            var on = Array.IndexOf(tokens, "on");
            AddRelation(tokens, on + 1, relations, classifications);
            classifications.Add(tokens.Contains("concurrently", StringComparer.Ordinal)
                ? SqlImpactKinds.CreateIndexConcurrent
                : SqlImpactKinds.CreateIndexNonConcurrent);
            return;
        }
        if (first is "update" or "delete" or "insert" or "truncate" || first == "with" &&
            tokens.Any(token => token is "update" or "delete" or "insert"))
        {
            var relationIndex = first switch
            {
                "update" => 1,
                "delete" => Array.IndexOf(tokens, "from") + 1,
                "insert" => Array.IndexOf(tokens, "into") + 1,
                "truncate" => tokens.ElementAtOrDefault(1) == "table" ? 2 : 1,
                _ => FindDmlRelationIndex(tokens),
            };
            AddRelation(tokens, relationIndex, relations, classifications);
            classifications.Add(SqlImpactKinds.LargeDml);
            return;
        }
        if (first == "select")
        {
            if (tokens.Any(token => token is "compress_chunk" or "decompress_chunk" or
                    "recompress_chunk" or "add_compression_policy" or
                    "remove_compression_policy"))
            {
                classifications.Add(SqlImpactKinds.TimescaleCompression);
                if (!AddLiteralRelations(tokens, relations))
                    classifications.Add(SqlImpactKinds.OpaqueOrUnknown);
            }
            else if (tokens.Any(token => token is "drop_chunks" or "move_chunk" or "alter_job"))
            {
                classifications.Add(SqlImpactKinds.TimescaleChunkOperation);
                if (!AddLiteralRelations(tokens, relations))
                    classifications.Add(SqlImpactKinds.OpaqueOrUnknown);
            }
            else
                classifications.Add(SqlImpactKinds.OpaqueOrUnknown);
            return;
        }
        if (first == "create" && tokens.ElementAtOrDefault(1) is
                ("table" or "type" or "view" or "sequence") ||
            first is "drop" or "grant" or "revoke" or "comment" or "analyze")
        {
            if (first == "create" && tokens.ElementAtOrDefault(1) == "table")
            {
                var index = tokens.ElementAtOrDefault(2) == "if" ? 5 : 2;
                AddRelation(tokens, index, relations, classifications);
            }
            classifications.Add(SqlImpactKinds.TransactionalDdl);
            return;
        }
        classifications.Add(SqlImpactKinds.OpaqueOrUnknown);
    }

    private static int FindDmlRelationIndex(string[] tokens)
    {
        for (var index = 0; index < tokens.Length - 1; index++)
            if (tokens[index] is "update" or "into" or "from") return index + 1;
        return -1;
    }

    private static void AddRelation(
        string[] tokens,
        int index,
        ISet<string> relations,
        ISet<string> classifications)
    {
        if (index < 0 || index + 2 >= tokens.Length || tokens[index] != "public" ||
            tokens[index + 1] != "." || !IsIdentifier(tokens[index + 2]))
        {
            classifications.Add(SqlImpactKinds.OpaqueOrUnknown);
            return;
        }
        relations.Add($"public.{tokens[index + 2]}");
    }

    private static bool IsIdentifier(string value) =>
        value.Length is >= 1 and <= 63 && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool AddLiteralRelations(string[] tokens, ISet<string> relations)
    {
        var observed = false;
        for (var index = 0; index + 5 < tokens.Length; index++)
            if (tokens[index] is "compress_chunk" or "decompress_chunk" or "recompress_chunk" or
                    "add_compression_policy" or "remove_compression_policy" or "drop_chunks" or
                    "move_chunk" && tokens[index + 1] == "(" &&
                tokens[index + 2] == "__relation_literal__" && tokens[index + 3] == "public" &&
                tokens[index + 4] == "." && IsIdentifier(tokens[index + 5]))
            {
                observed = true;
                relations.Add($"public.{tokens[index + 5]}");
            }
        return observed;
    }

    private static bool ContainsSequence(string[] values, params string[] expected)
    {
        for (var index = 0; index <= values.Length - expected.Length; index++)
            if (values.AsSpan(index, expected.Length).SequenceEqual(expected)) return true;
        return false;
    }

    private static string Sanitize(string sql)
    {
        var result = new StringBuilder(sql.Length);
        for (var index = 0; index < sql.Length;)
        {
            var character = sql[index];
            if (character == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\r' or '\n')) index++;
                result.Append(' ');
                continue;
            }
            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var depth = 1;
                index += 2;
                while (index < sql.Length && depth > 0)
                {
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else index++;
                }
                if (depth != 0) throw new MigratorRejectedException("migration_sql_lexically_invalid");
                result.Append(' ');
                continue;
            }
            if (character == '\'' || character == '"')
            {
                var quote = character;
                index++;
                var closed = false;
                var literal = new StringBuilder();
                while (index < sql.Length)
                {
                    if (sql[index] == quote)
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == quote)
                        {
                            literal.Append(quote);
                            index += 2;
                            continue;
                        }
                        index++;
                        closed = true;
                        break;
                    }
                    literal.Append(sql[index]);
                    index++;
                }
                if (!closed) throw new MigratorRejectedException("migration_sql_lexically_invalid");
                if (quote == '\'' && TryParsePublicRelation(literal.ToString(), out var relation))
                    result.Append(" __relation_literal__ ").Append(relation).Append(' ');
                else
                    result.Append(quote == '"' ? " __quoted_identifier__ " : " __literal__ ");
                continue;
            }
            if (character == '$')
            {
                var tagEnd = sql.IndexOf('$', index + 1);
                if (tagEnd >= 0 && IsDollarTag(sql, index + 1, tagEnd - index - 1))
                {
                    var tag = sql[index..(tagEnd + 1)];
                    var bodyEnd = sql.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                    if (bodyEnd < 0)
                        throw new MigratorRejectedException("migration_sql_lexically_invalid");
                    index = bodyEnd + tag.Length;
                    result.Append(" __dollar_body__ ");
                    continue;
                }
            }
            result.Append(char.ToLowerInvariant(character));
            index++;
        }
        return result.ToString();
    }

    private static bool IsDollarTag(string value, int start, int length)
    {
        for (var index = start; index < start + length; index++)
            if (!char.IsAsciiLetterOrDigit(value[index]) && value[index] != '_') return false;
        return true;
    }

    private static bool TryParsePublicRelation(string value, out string relation)
    {
        var parts = value.Split('.', StringSplitOptions.None);
        if (parts is ["public", var table] && IsIdentifier(table))
        {
            relation = value;
            return true;
        }
        relation = string.Empty;
        return false;
    }
}

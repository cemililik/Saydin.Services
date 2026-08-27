using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseMigrator.Tests;

[Collection("migration-integration")]
public sealed class InstallationCredentialRehashMigrationIntegrationTests
{
    [SkippableFact]
    public async Task RehashResolver_IsConcurrentIdempotentAndStrictlyActiveOnly()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        var migration = await new MigrationRunner(
            database.Options(TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        if (migration.BackupPostBootstrapRequired)
            await database.EnsureRolesAsync();

        await using var dataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(
            database.RuntimeOptions(LoginPurpose.Api));
        var live = await RegisterAsync(dataSource, 0x11);
        var activeHash = Hash(0x91);

        var results = await Task.WhenAll(
            ResolveAndRehashAsync(dataSource, live.Hash, 1, activeHash, 2),
            ResolveAndRehashAsync(dataSource, live.Hash, 1, activeHash, 2));

        results.Should().Equal(live.PrincipalId, live.PrincipalId);
        await AssertVerifierAsync(database, live.CredentialId, activeHash, 2);
        (await ResolveAndRehashAsync(dataSource, activeHash, 2, activeHash, 2))
            .Should().Be(live.PrincipalId,
                "a token upgraded on use must survive removal of the old key version");

        var revoked = await RegisterAsync(dataSource, 0x12);
        await ExecuteAsync(dataSource,
            "SELECT * FROM public.revoke_installation($1,$2)", revoked.Hash, (short)1);
        (await ResolveAndRehashAsync(dataSource, revoked.Hash, 1, Hash(0x92), 2))
            .Should().BeNull();
        await AssertVerifierAsync(database, revoked.CredentialId, revoked.Hash, 1);

        var expired = await RegisterAsync(dataSource, 0x13);
        await database.ExecuteAsync($"""
            UPDATE public.installation_credentials
               SET issued_at=clock_timestamp()-interval '2 hours',
                   activated_at=clock_timestamp()-interval '2 hours',
                   expires_at=clock_timestamp()-interval '1 hour'
             WHERE id='{expired.CredentialId:D}'
            """);
        (await ResolveAndRehashAsync(dataSource, expired.Hash, 1, Hash(0x93), 2))
            .Should().BeNull();
        await AssertVerifierAsync(database, expired.CredentialId, expired.Hash, 1);

        var rotating = await RegisterAsync(dataSource, 0x14);
        var rotationId = Guid.CreateVersion7();
        var pendingId = Guid.CreateVersion7();
        var pendingHash = Hash(0x24);
        await ExecuteAsync(dataSource, """
            SELECT * FROM public.begin_installation_rotation($1,$2,$3,$4,$5,$6)
            """, rotating.Hash, (short)1, rotationId, pendingId, pendingHash, (short)1);
        (await ResolveAndRehashAsync(dataSource, pendingHash, 1, Hash(0x94), 2))
            .Should().BeNull("pending credentials must never be rehashed by normal resolution");
        await AssertVerifierAsync(database, pendingId, pendingHash, 1);
        (await ResolvePendingAsync(dataSource, Guid.CreateVersion7(), pendingHash, 1))
            .Should().BeNull("a pending credential cannot cross its exact rotation binding");

        (await database.ScalarAsync<bool>($"""
            SELECT NOT has_table_privilege(
                       '{database.Contract.ApiCapability.Name}',
                       'public.installation_credentials','UPDATE')
               AND has_function_privilege(
                       '{database.Contract.ApiCapability.Name}',
                       'public.resolve_installation_and_rehash(bytea,smallint,bytea,smallint)',
                       'EXECUTE')
            """)).Should().BeTrue();
    }

    private static async Task<(Guid PrincipalId, Guid CredentialId, byte[] Hash)> RegisterAsync(
        NpgsqlDataSource dataSource, byte marker)
    {
        var principalId = Guid.CreateVersion7();
        var credentialId = Guid.CreateVersion7();
        var hash = Hash(marker);
        await ExecuteAsync(dataSource,
            "SELECT * FROM public.register_installation($1,$2,$3,$4)",
            principalId, credentialId, hash, (short)1);
        return (principalId, credentialId, hash);
    }

    private static async Task<Guid?> ResolveAndRehashAsync(
        NpgsqlDataSource dataSource, byte[] acceptedHash, short acceptedVersion,
        byte[] activeHash, short activeVersion)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT principal_id
              FROM public.resolve_installation_and_rehash($1,$2,$3,$4)
            """, connection);
        Add(command, acceptedHash, acceptedVersion, activeHash, activeVersion);
        return await command.ExecuteScalarAsync() is Guid value ? value : null;
    }

    private static async Task<Guid?> ResolvePendingAsync(
        NpgsqlDataSource dataSource, Guid rotationId, byte[] hash, short version)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT principal_id
              FROM public.resolve_installation_rotation_commit($1,$2,$3)
            """, connection);
        Add(command, rotationId, hash, version);
        return await command.ExecuteScalarAsync() is Guid value ? value : null;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql,
        params object[] parameters)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        Add(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static void Add(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = value switch
                {
                    byte[] => NpgsqlDbType.Bytea,
                    short => NpgsqlDbType.Smallint,
                    Guid => NpgsqlDbType.Uuid,
                    _ => throw new ArgumentOutOfRangeException(nameof(values)),
                },
                Value = value,
            });
    }

    private static async Task AssertVerifierAsync(
        TestDatabase database, Guid credentialId, byte[] hash, short version)
    {
        (await database.ScalarAsync<bool>($"""
            SELECT hash_key_version={version}
               AND secret_hash=pg_catalog.decode('{Convert.ToHexStringLower(hash)}','hex')
              FROM public.installation_credentials
             WHERE id='{credentialId:D}'
            """)).Should().BeTrue();
    }

    private static byte[] Hash(byte marker) => Enumerable.Repeat(marker, 32).ToArray();
}

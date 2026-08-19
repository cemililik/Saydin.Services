using Npgsql;
using NpgsqlTypes;
using Saydin.Api.Services;

namespace Saydin.Api.Repositories;

public sealed class InstallationRepository(NpgsqlDataSource dataSource) : IInstallationRepository
{
    public Task<InstallationPrincipal> RegisterAsync(
        Guid principalId,
        Guid credentialId,
        CredentialHashCandidate credential,
        CancellationToken ct) => ExecuteRequiredAsync(
            "SELECT * FROM public.register_installation($1, $2, $3, $4)",
            [Uuid(principalId), Uuid(credentialId), Bytes(credential.SecretHash), SmallInt(credential.KeyVersion)],
            ct);

    public async Task<InstallationPrincipal?> ResolveAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            var resolved = await ExecuteOptionalAsync(
                "SELECT * FROM public.resolve_installation($1, $2)",
                [Bytes(candidate.SecretHash), SmallInt(candidate.KeyVersion)],
                ct);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    public Task<InstallationPrincipal> BeginRotationAsync(
        CredentialHashCandidate currentCredential,
        Guid rotationId,
        Guid newCredentialId,
        CredentialHashCandidate newCredential,
        CancellationToken ct) => ExecuteRequiredAsync(
            "SELECT * FROM public.begin_installation_rotation($1, $2, $3, $4, $5, $6)",
            [
                Bytes(currentCredential.SecretHash), SmallInt(currentCredential.KeyVersion),
                Uuid(rotationId), Uuid(newCredentialId),
                Bytes(newCredential.SecretHash), SmallInt(newCredential.KeyVersion),
            ],
            ct);

    public Task<InstallationPrincipal> CommitRotationAsync(
        Guid rotationId,
        CredentialHashCandidate newCredential,
        CancellationToken ct) => ExecuteRequiredAsync(
            "SELECT * FROM public.commit_installation_rotation($1, $2, $3)",
            [Uuid(rotationId), Bytes(newCredential.SecretHash), SmallInt(newCredential.KeyVersion)],
            ct);

    public async Task RevokeAsync(CredentialHashCandidate currentCredential, CancellationToken ct)
        => _ = await ExecuteRequiredAsync(
            "SELECT * FROM public.revoke_installation($1, $2)",
            [Bytes(currentCredential.SecretHash), SmallInt(currentCredential.KeyVersion)],
            ct);

    private async Task<InstallationPrincipal> ExecuteRequiredAsync(
        string sql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken ct)
        => await ExecuteOptionalAsync(sql, parameters, ct)
           ?? throw new InvalidOperationException("Installation database contract returned no row.");

    private async Task<InstallationPrincipal?> ExecuteOptionalAsync(
        string sql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var result = new InstallationPrincipal(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));

        if (await reader.ReadAsync(ct))
            throw new InvalidOperationException("Installation database contract returned multiple rows.");

        return result;
    }

    private static NpgsqlParameter Uuid(Guid value) => new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value };
    private static NpgsqlParameter Bytes(byte[] value) => new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = value };
    private static NpgsqlParameter SmallInt(short value) => new() { NpgsqlDbType = NpgsqlDbType.Smallint, Value = value };
}

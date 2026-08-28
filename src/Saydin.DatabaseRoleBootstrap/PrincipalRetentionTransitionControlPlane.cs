using Npgsql;

namespace Saydin.DatabaseRoleBootstrap;

internal sealed partial class RoleBootstrapRunner
{
    private const string PrincipalRetentionTransitionSchema =
        "saydin_principal_retention_control";
    private const string PrincipalRetentionTransitionFunction =
        "consume_principal_retention_transition";
    private const string PrincipalRetentionTransitionMarker =
        "saydin-role-bootstrap/v1;purpose=principal-retention-transition;kind=one-shot";

    private async Task EnsurePrincipalRetentionTransitionControlPlaneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        if (await PrincipalRetentionAppliedAsync(connection, transaction, cancellationToken))
        {
            await VerifyPrincipalRetentionTransitionAbsenceAsync(
                connection, transaction, cancellationToken);
            return;
        }

        if (await ScalarBoolAsync(connection, transaction,
                "SELECT pg_catalog.to_regnamespace($1) IS NULL", cancellationToken,
                PrincipalRetentionTransitionSchema))
        {
            var body = BuildPrincipalRetentionTransitionBody(adminRole);
            await ExecuteSqlAsync(connection, transaction, $"""
                CREATE SCHEMA {QuoteIdentifier(PrincipalRetentionTransitionSchema)}
                    AUTHORIZATION {QuoteIdentifier(adminRole)};
                COMMENT ON SCHEMA {QuoteIdentifier(PrincipalRetentionTransitionSchema)} IS
                    {QuoteLiteral(PrincipalRetentionTransitionMarker)};
                REVOKE ALL ON SCHEMA {QuoteIdentifier(PrincipalRetentionTransitionSchema)} FROM PUBLIC;
                GRANT USAGE ON SCHEMA {QuoteIdentifier(PrincipalRetentionTransitionSchema)}
                    TO {QuoteIdentifier(options.Contract.Owner.Name)};
                CREATE FUNCTION {QuoteIdentifier(PrincipalRetentionTransitionSchema)}.
                    {QuoteIdentifier(PrincipalRetentionTransitionFunction)}()
                RETURNS void LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO pg_catalog, pg_temp
                AS {QuoteLiteral(body)};
                REVOKE ALL ON FUNCTION {QuoteIdentifier(PrincipalRetentionTransitionSchema)}.
                    {QuoteIdentifier(PrincipalRetentionTransitionFunction)}() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION {QuoteIdentifier(PrincipalRetentionTransitionSchema)}.
                    {QuoteIdentifier(PrincipalRetentionTransitionFunction)}()
                    TO {QuoteIdentifier(options.Contract.Owner.Name)};
                """, cancellationToken);
        }

        await VerifyPrincipalRetentionTransitionAsync(
            connection, transaction, adminRole, cancellationToken);
    }

    private async Task VerifyPrincipalRetentionTransitionControlPlaneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        if (await PrincipalRetentionAppliedAsync(connection, transaction, cancellationToken))
            await VerifyPrincipalRetentionTransitionAbsenceAsync(
                connection, transaction, cancellationToken);
        else
            await VerifyPrincipalRetentionTransitionAsync(
                connection, transaction, adminRole, cancellationToken);
    }

    private static async Task<bool> PrincipalRetentionAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await ScalarBoolAsync(connection, transaction,
                "SELECT pg_catalog.to_regclass('public.schema_migrations') IS NULL",
                cancellationToken))
            return false;
        return await ScalarBoolAsync(connection, transaction, """
            SELECT pg_catalog.count(*)=1 AND pg_catalog.bool_and(state='succeeded')
              FROM public.schema_migrations
             WHERE version='022_principal_retention'
            """, cancellationToken);
    }

    private async Task VerifyPrincipalRetentionTransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        var body = BuildPrincipalRetentionTransitionBody(adminRole);
        if (!await ScalarBoolAsync(connection, transaction, """
                WITH schema_acl AS (
                    SELECT coalesce(grantee.rolname,'PUBLIC') AS grantee,
                           grantor.rolname AS grantor,acl.privilege_type,acl.is_grantable
                      FROM pg_catalog.pg_namespace namespace
                      CROSS JOIN LATERAL pg_catalog.aclexplode(
                          coalesce(namespace.nspacl,
                              pg_catalog.acldefault('n',namespace.nspowner))) acl
                      LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                      LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                     WHERE namespace.nspname=$1),
                expected_schema(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                    ($2,$2,'CREATE',false),($2,$2,'USAGE',false),($3,$2,'USAGE',false)),
                function_acl AS (
                    SELECT coalesce(grantee.rolname,'PUBLIC') AS grantee,
                           grantor.rolname AS grantor,acl.privilege_type,acl.is_grantable
                      FROM pg_catalog.pg_proc function
                      CROSS JOIN LATERAL pg_catalog.aclexplode(
                          coalesce(function.proacl,
                              pg_catalog.acldefault('f',function.proowner))) acl
                      LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                      LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                     WHERE function.oid=($1||'.consume_principal_retention_transition()')::pg_catalog.regprocedure),
                expected_function(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                    ($2,$2,'EXECUTE',false),($3,$2,'EXECUTE',false))
                SELECT (SELECT pg_catalog.count(*)=1 AND pg_catalog.bool_and(
                                   pg_catalog.pg_get_userbyid(namespace.nspowner)=$2
                                   AND pg_catalog.obj_description(
                                       namespace.oid,'pg_namespace')=$4)
                          FROM pg_catalog.pg_namespace namespace WHERE namespace.nspname=$1)
                   AND (SELECT pg_catalog.count(*)=1 AND pg_catalog.bool_and(
                                   pg_catalog.pg_get_userbyid(function.proowner)=$2
                                   AND function.prosecdef AND NOT function.proisstrict
                                   AND function.prokind='f' AND function.provolatile='v'
                                   AND function.proparallel='u' AND NOT function.proleakproof
                                   AND function.proconfig=
                                       ARRAY['search_path=pg_catalog, pg_temp']::text[]
                                   AND function.prosrc=$5)
                          FROM pg_catalog.pg_proc function
                         WHERE function.oid=($1||'.consume_principal_retention_transition()')::pg_catalog.regprocedure)
                   AND NOT EXISTS ((SELECT * FROM schema_acl EXCEPT ALL SELECT * FROM expected_schema)
                                   UNION ALL
                                   (SELECT * FROM expected_schema EXCEPT ALL SELECT * FROM schema_acl))
                   AND NOT EXISTS ((SELECT * FROM function_acl EXCEPT ALL SELECT * FROM expected_function)
                                   UNION ALL
                                   (SELECT * FROM expected_function EXCEPT ALL SELECT * FROM function_acl))
                """, cancellationToken, PrincipalRetentionTransitionSchema, adminRole,
                options.Contract.Owner.Name, PrincipalRetentionTransitionMarker, body))
            throw TopologyRejected("principal_retention_transition_contract_mismatch");
    }

    private static async Task VerifyPrincipalRetentionTransitionAbsenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await ScalarBoolAsync(connection, transaction,
                "SELECT pg_catalog.to_regnamespace($1) IS NULL", cancellationToken,
                PrincipalRetentionTransitionSchema))
            throw TopologyRejected("principal_retention_transition_not_consumed");
    }

    private string BuildPrincipalRetentionTransitionBody(string adminRole)
    {
        var migratorPattern = "^" + options.Contract.Prefix +
                              "_migrator_login_v[1-9][0-9]{0,2}$";
        return $"""
            DECLARE
                owner_role text;
                scheduler_role text;
            BEGIN
                SELECT contract.owner_role,contract.timescale_scheduler_role
                  INTO owner_role,scheduler_role
                  FROM public.saydin_role_contract contract
                 WHERE contract.singleton=1
                   AND contract.contract_schema_version=1
                   AND contract.contract_sha256={QuoteLiteral(options.ContractSha256)}
                   AND contract.deployment_id={QuoteLiteral(options.Contract.DeploymentId)}
                   AND contract.database_name=pg_catalog.current_database()
                   AND contract.system_identifier_sha256=
                       {QuoteLiteral(options.Contract.SystemIdentifierSha256)}
                   AND contract.role_prefix={QuoteLiteral(options.Contract.Prefix)}
                   AND contract.owner_role={QuoteLiteral(options.Contract.Owner.Name)}
                   AND contract.timescale_scheduler_role=
                       {QuoteLiteral(options.Contract.TimescaleScheduler.Name)};
                IF owner_role IS NULL OR scheduler_role IS NULL
                   OR CURRENT_USER IS DISTINCT FROM {QuoteLiteral(adminRole)}
                   OR (session_user IS DISTINCT FROM {QuoteLiteral(adminRole)} AND NOT EXISTS (
                        SELECT 1
                          FROM pg_catalog.pg_auth_members membership
                          JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
                          JOIN pg_catalog.pg_roles caller ON caller.oid=membership.member
                         WHERE granted.rolname=owner_role
                           AND caller.rolname=session_user
                           AND caller.rolname ~ {QuoteLiteral(migratorPattern)}
                           AND NOT membership.admin_option
                           AND NOT membership.inherit_option
                           AND membership.set_option))
                   OR (SELECT pg_catalog.count(*) FROM public.schema_migrations
                        WHERE version='022_principal_retention' AND state='running')<>1
                   OR (SELECT pg_catalog.count(*) FROM pg_catalog.pg_constraint constraint_row
                        WHERE constraint_row.conrelid='public.activity_logs'::pg_catalog.regclass
                          AND constraint_row.conname='activity_logs_user_id_fkey'
                          AND constraint_row.contype='f'
                          AND constraint_row.confrelid='public.users'::pg_catalog.regclass
                          AND constraint_row.convalidated AND NOT constraint_row.condeferrable
                          AND constraint_row.confdeltype='a')<>1
                   OR (SELECT pg_catalog.count(*) FROM timescaledb_information.hypertables hypertable
                        WHERE hypertable.hypertable_schema='public'
                          AND hypertable.hypertable_name='activity_logs'
                          AND NOT hypertable.compression_enabled)<>1 THEN
                    RAISE EXCEPTION 'principal retention transition target rejected'
                        USING ERRCODE='42501';
                END IF;

                EXECUTE 'ALTER TABLE public.activity_logs SET (
                    timescaledb.compress,
                    timescaledb.compress_segmentby=''action'',
                    timescaledb.compress_orderby=''created_at DESC'')';

                EXECUTE pg_catalog.format(
                    'GRANT CREATE ON SCHEMA public TO %I',scheduler_role);
                EXECUTE pg_catalog.format(
                    'GRANT CREATE ON SCHEMA _timescaledb_internal TO %I',scheduler_role);
                EXECUTE pg_catalog.format(
                    'ALTER TABLE public.activity_logs OWNER TO %I',scheduler_role);
                EXECUTE pg_catalog.format(
                    'REVOKE CREATE ON SCHEMA _timescaledb_internal FROM %I',scheduler_role);
                EXECUTE pg_catalog.format(
                    'REVOKE CREATE ON SCHEMA public FROM %I',scheduler_role);

                IF (SELECT pg_catalog.count(*) FROM timescaledb_information.hypertables hypertable
                     WHERE hypertable.hypertable_schema='public'
                       AND hypertable.hypertable_name='activity_logs'
                       AND hypertable.compression_enabled)<>1
                   OR pg_catalog.pg_get_userbyid(
                          (SELECT relation.relowner FROM pg_catalog.pg_class relation
                            WHERE relation.oid='public.activity_logs'::pg_catalog.regclass))
                      IS DISTINCT FROM scheduler_role
                   OR EXISTS (
                        SELECT 1
                          FROM _timescaledb_catalog.hypertable source
                          JOIN _timescaledb_catalog.hypertable compressed
                            ON compressed.id=source.compressed_hypertable_id
                          JOIN pg_catalog.pg_namespace namespace
                            ON namespace.nspname=compressed.schema_name
                          JOIN pg_catalog.pg_class relation
                            ON relation.relnamespace=namespace.oid
                           AND relation.relname=compressed.table_name
                         WHERE source.schema_name='public'
                           AND source.table_name='activity_logs'
                           AND pg_catalog.pg_get_userbyid(relation.relowner)
                               IS DISTINCT FROM scheduler_role) THEN
                    RAISE EXCEPTION 'principal retention compression owner rejected'
                        USING ERRCODE='42501';
                END IF;

                EXECUTE pg_catalog.format(
                    'ALTER FUNCTION {PrincipalRetentionTransitionSchema}.' ||
                    '{PrincipalRetentionTransitionFunction}() OWNER TO %I',owner_role);
                EXECUTE pg_catalog.format(
                    'ALTER SCHEMA {PrincipalRetentionTransitionSchema} OWNER TO %I',owner_role);
            END
            """;
    }
}

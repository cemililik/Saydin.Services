using System.Globalization;
using Npgsql;

namespace Saydin.DatabaseRoleBootstrap;

internal sealed partial class RoleBootstrapRunner
{
    private const string TransitionSchema = "saydin_role_control";
    private const string TransitionFunction = "consume_timescale_scheduler_transition";
    private const string TransitionMarker =
        "saydin-role-bootstrap/v1;purpose=timescale-owner-transition;kind=one-shot";

    private async Task EnsureTimescaleTransitionControlPlaneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        var phase = await ReadPrivilegePhaseAsync(connection, transaction, cancellationToken);
        if (phase == PrivilegePhase.Post019)
        {
            await VerifyPost019TransitionAbsenceAsync(
                connection, transaction, adminRole, cancellationToken);
            return;
        }

        if (await ScalarBoolAsync(connection, transaction,
                "SELECT to_regnamespace($1) IS NULL", cancellationToken, TransitionSchema))
        {
            var scheduler = QuoteIdentifier(options.Contract.TimescaleScheduler.Name);
            var owner = QuoteIdentifier(options.Contract.Owner.Name);
            var body = BuildTransitionBody(adminRole);
            await ExecuteSqlAsync(connection, transaction, $"""
                GRANT CREATE ON SCHEMA _timescaledb_internal TO {scheduler};
                CREATE SCHEMA {QuoteIdentifier(TransitionSchema)} AUTHORIZATION {QuoteIdentifier(adminRole)};
                COMMENT ON SCHEMA {QuoteIdentifier(TransitionSchema)} IS {QuoteLiteral(TransitionMarker)};
                REVOKE ALL ON SCHEMA {QuoteIdentifier(TransitionSchema)} FROM PUBLIC;
                GRANT USAGE ON SCHEMA {QuoteIdentifier(TransitionSchema)} TO {owner};
                CREATE FUNCTION {QuoteIdentifier(TransitionSchema)}.{QuoteIdentifier(TransitionFunction)}()
                RETURNS void LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO pg_catalog, pg_temp
                AS {QuoteLiteral(body)};
                REVOKE ALL ON FUNCTION {QuoteIdentifier(TransitionSchema)}.{QuoteIdentifier(TransitionFunction)}()
                    FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION {QuoteIdentifier(TransitionSchema)}.{QuoteIdentifier(TransitionFunction)}()
                    TO {owner};
                """, cancellationToken);
        }

        await VerifyPre019TransitionAsync(connection, transaction, adminRole, cancellationToken);
    }

    private async Task VerifyTimescaleTransitionControlPlaneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        var phase = await ReadPrivilegePhaseAsync(connection, transaction, cancellationToken);
        if (phase == PrivilegePhase.Pre019)
            await VerifyPre019TransitionAsync(connection, transaction, adminRole, cancellationToken);
        else
            await VerifyPost019TransitionAbsenceAsync(
                connection, transaction, adminRole, cancellationToken);
    }

    private async Task<PrivilegePhase> ReadPrivilegePhaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var contractExists = !await ScalarBoolAsync(connection, transaction,
            "SELECT to_regclass('public.saydin_role_contract') IS NULL", cancellationToken);
        if (!contractExists)
        {
            var migrationsExist = !await ScalarBoolAsync(connection, transaction,
                "SELECT to_regclass('public.schema_migrations') IS NULL", cancellationToken);
            if (migrationsExist && await ScalarBoolAsync(connection, transaction, """
                    SELECT EXISTS (SELECT 1 FROM public.schema_migrations
                                   WHERE version='019_privilege_separation'
                                     AND state='succeeded')
                    """, cancellationToken))
                throw TopologyRejected("privilege_phase_ambiguous");
            return PrivilegePhase.Pre019;
        }

        if (!await ScalarBoolAsync(connection, transaction, """
                SELECT (SELECT count(*)=1 AND bool_and(
                                   contract_schema_version=1 AND contract_sha256=$1
                                   AND deployment_id=$2 AND database_name=current_database()
                                   AND system_identifier_sha256=$3 AND role_prefix=$4
                                   AND owner_role=$5 AND timescale_scheduler_role=$6)
                          FROM public.saydin_role_contract)
                   AND (SELECT count(*)=1 AND bool_and(state='succeeded')
                          FROM public.schema_migrations
                         WHERE version='019_privilege_separation')
                   AND (SELECT count(*)=1 AND bool_and(state='ready')
                          FROM public.saydin_migration_control WHERE singleton=1)
                """, cancellationToken,
                options.ContractSha256, options.Contract.DeploymentId,
                options.Contract.SystemIdentifierSha256, options.Contract.Prefix,
                options.Contract.Owner.Name, options.Contract.TimescaleScheduler.Name))
            throw TopologyRejected("privilege_phase_ambiguous");
        return PrivilegePhase.Post019;
    }

    private async Task VerifyPre019TransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        var body = BuildTransitionBody(adminRole);
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
                     WHERE function.oid=($1||'.consume_timescale_scheduler_transition()')::regprocedure),
                expected_function(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                    ($2,$2,'EXECUTE',false),($3,$2,'EXECUTE',false)),
                internal_acl AS (
                    SELECT coalesce(grantee.rolname,'PUBLIC') AS grantee,
                           grantor.rolname AS grantor,acl.privilege_type,acl.is_grantable
                      FROM pg_catalog.pg_namespace namespace
                      CROSS JOIN LATERAL pg_catalog.aclexplode(
                          coalesce(namespace.nspacl,
                                   pg_catalog.acldefault('n',namespace.nspowner))) acl
                      LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                      LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                     WHERE namespace.nspname='_timescaledb_internal'),
                expected_internal(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                    ($2,$2,'CREATE',false),($2,$2,'USAGE',false),('PUBLIC',$2,'USAGE',false),
                    ($4,$2,'CREATE',false))
                SELECT (SELECT pg_catalog.pg_get_userbyid(nspowner)=$2
                               AND pg_catalog.obj_description(oid,'pg_namespace')=$5
                          FROM pg_catalog.pg_namespace WHERE nspname=$1)
                   AND (SELECT pg_catalog.pg_get_userbyid(function.proowner)=$2
                               AND function.prosecdef
                               AND function.proconfig=ARRAY['search_path=pg_catalog, pg_temp']::text[]
                               AND function.prosrc=$6
                          FROM pg_catalog.pg_proc function
                         WHERE function.oid=($1||'.consume_timescale_scheduler_transition()')::regprocedure)
                   AND NOT EXISTS ((SELECT * FROM schema_acl EXCEPT ALL SELECT * FROM expected_schema)
                                   UNION ALL
                                   (SELECT * FROM expected_schema EXCEPT ALL SELECT * FROM schema_acl))
                   AND NOT EXISTS ((SELECT * FROM function_acl EXCEPT ALL SELECT * FROM expected_function)
                                   UNION ALL
                                   (SELECT * FROM expected_function EXCEPT ALL SELECT * FROM function_acl))
                   AND NOT EXISTS ((SELECT * FROM internal_acl EXCEPT ALL SELECT * FROM expected_internal)
                                   UNION ALL
                                   (SELECT * FROM expected_internal EXCEPT ALL SELECT * FROM internal_acl))
                """, cancellationToken,
                TransitionSchema, adminRole, options.Contract.Owner.Name,
                options.Contract.TimescaleScheduler.Name, TransitionMarker, body))
            throw TopologyRejected("timescale_transition_contract_mismatch");
    }

    private async Task VerifyPost019TransitionAbsenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        if (!await ScalarBoolAsync(connection, transaction, """
                WITH target AS (
                    SELECT namespace.nspowner,namespace.nspacl,extension.extowner
                      FROM pg_catalog.pg_namespace namespace
                      JOIN pg_catalog.pg_extension extension ON extension.extname='timescaledb'
                     WHERE namespace.nspname='_timescaledb_internal'),
                actual AS (
                    SELECT coalesce(grantee.rolname,'PUBLIC') AS grantee,
                           grantor.rolname AS grantor,acl.privilege_type,acl.is_grantable
                      FROM target
                      CROSS JOIN LATERAL pg_catalog.aclexplode(
                          coalesce(target.nspacl,
                              pg_catalog.acldefault('n',target.nspowner))) acl
                      LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                      LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor),
                expected_owner(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                    ($3,$3,'CREATE',false),($3,$3,'USAGE',false)),
                usage_roles(grantee) AS (
                    SELECT unnest(ARRAY[$2,$4,$5,$6,$7,$8,$9,$10]::text[])),
                expected AS (
                    SELECT * FROM expected_owner
                    UNION ALL
                    SELECT usage_roles.grantee,$3::text,'USAGE'::text,false
                      FROM usage_roles)
                SELECT to_regnamespace($1) IS NULL
                   AND (SELECT count(*)=1 AND bool_and(nspowner=extowner)
                          FROM target)
                   AND NOT EXISTS ((SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)
                                   UNION ALL
                                   (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual))
                """, cancellationToken, TransitionSchema,
                options.Contract.TimescaleScheduler.Name, adminRole,
                options.Contract.MigratorCapability.Name,
                options.Contract.ApiCapability.Name,
                options.Contract.IngestionCapability.Name,
                options.Contract.CalendarImporterCapability.Name,
                options.Contract.ExporterCapability.Name,
                options.Contract.AuditCapability.Name,
                options.Contract.Owner.Name))
            throw TopologyRejected("timescale_transition_not_consumed");
    }

    private string BuildTransitionBody(string adminRole)
    {
        const string ownerMarkerSuffix = "purpose=owner;kind=owner";
        var markerPrefix = options.Contract.Owner.Marker[..^ownerMarkerSuffix.Length];
        const string versionSuffixPattern = "_login_v([1-9][0-9]{0,2})$";
        var internalUsageRoles = options.Contract.Capabilities
            .Append(options.Contract.Owner)
            .Append(options.Contract.TimescaleScheduler)
            .Select(role => role.Name)
            .ToArray();
        var internalUsageTargets = string.Join(", ",
            internalUsageRoles.Select(QuoteIdentifier));
        var terminalUsageAcl = string.Join(",\n                        ",
            internalUsageRoles.Select(role =>
                $"({QuoteLiteral(role)},{QuoteLiteral(adminRole)},'USAGE',false)"));
        return $"""
            BEGIN
                IF pg_catalog.current_setting('saydin.role_contract_sha256',true)
                       IS DISTINCT FROM {QuoteLiteral(options.ContractSha256)}
                   OR pg_catalog.current_setting('saydin.system_identifier_sha256',true)
                       IS DISTINCT FROM {QuoteLiteral(options.Contract.SystemIdentifierSha256)}
                   OR pg_catalog.current_setting('saydin.role_prefix',true)
                       IS DISTINCT FROM {QuoteLiteral(options.Contract.Prefix)}
                   OR pg_catalog.current_setting('saydin.owner_role',true)
                       IS DISTINCT FROM {QuoteLiteral(options.Contract.Owner.Name)}
                   OR pg_catalog.current_setting('saydin.timescale_scheduler_role',true)
                       IS DISTINCT FROM {QuoteLiteral(options.Contract.TimescaleScheduler.Name)}
                   OR pg_catalog.current_database() IS DISTINCT FROM {QuoteLiteral(options.Contract.Database)}
                   OR (SELECT pg_catalog.encode(pg_catalog.sha256(
                           pg_catalog.convert_to(system_identifier::text,'UTF8')),'hex')
                         FROM pg_catalog.pg_control_system())
                       IS DISTINCT FROM {QuoteLiteral(options.Contract.SystemIdentifierSha256)} THEN
                    RAISE EXCEPTION 'transition target contract rejected' USING ERRCODE='42501';
                END IF;
                IF pg_catalog.to_regclass('public.saydin_role_contract') IS NULL
                   OR (SELECT pg_catalog.count(*) FROM public.saydin_role_contract)<>0
                   OR pg_catalog.to_regclass('public.schema_migrations') IS NULL
                   OR (SELECT pg_catalog.count(*) FROM public.schema_migrations
                        WHERE version='019_privilege_separation' AND state='running')<>1 THEN
                    RAISE EXCEPTION 'transition phase rejected' USING ERRCODE='55000';
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_roles role
                    JOIN pg_catalog.pg_authid auth ON auth.oid=role.oid
                     WHERE role.rolname={QuoteLiteral(options.Contract.TimescaleScheduler.Name)}
                       AND role.rolcanlogin AND NOT role.rolsuper AND NOT role.rolcreatedb
                       AND NOT role.rolcreaterole AND NOT role.rolinherit
                       AND NOT role.rolreplication AND NOT role.rolbypassrls
                       AND role.rolconnlimit=0 AND role.rolvaliduntil IS NULL
                       AND role.rolconfig IS NULL AND auth.rolpassword IS NULL
                       AND pg_catalog.shobj_description(role.oid,'pg_authid')=
                           {QuoteLiteral(options.Contract.TimescaleScheduler.Marker)}) THEN
                    RAISE EXCEPTION 'transition scheduler contract rejected' USING ERRCODE='42501';
                END IF;
                IF pg_catalog.current_setting('saydin.legacy_privilege_cutover',true)='off' THEN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_roles role
                         WHERE role.rolname=session_user
                           AND role.rolname ~ {QuoteLiteral("^" + options.Contract.Prefix + "_migrator_login_v[1-9][0-9]{0,2}$")}
                           AND pg_catalog.shobj_description(role.oid,'pg_authid')=
                               {QuoteLiteral(markerPrefix + "purpose=migrator;kind=login;version=")} ||
                               pg_catalog.substring(role.rolname,{QuoteLiteral(versionSuffixPattern)})) THEN
                        RAISE EXCEPTION 'transition caller rejected' USING ERRCODE='42501';
                    END IF;
                ELSIF session_user IS DISTINCT FROM {QuoteLiteral(adminRole)}
                      OR (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=session_user)<>10 THEN
                    RAISE EXCEPTION 'transition legacy caller rejected' USING ERRCODE='42501';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                      FROM pg_catalog.pg_auth_members membership
                      JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
                      JOIN pg_catalog.pg_roles member ON member.oid=membership.member
                     WHERE granted.rolname={QuoteLiteral(options.Contract.TimescaleScheduler.Name)}
                       AND member.rolname={QuoteLiteral(options.Contract.Owner.Name)}
                       AND membership.grantor=10 AND NOT membership.admin_option
                       AND NOT membership.inherit_option AND membership.set_option) THEN
                    RAISE EXCEPTION 'transition membership rejected' USING ERRCODE='42501';
                END IF;
                IF (WITH actual AS (
                        SELECT coalesce(grantee.rolname,'PUBLIC') AS grantee,
                               grantor.rolname AS grantor,acl.privilege_type,acl.is_grantable
                          FROM pg_catalog.pg_namespace namespace
                          CROSS JOIN LATERAL pg_catalog.aclexplode(
                              coalesce(namespace.nspacl,
                                  pg_catalog.acldefault('n',namespace.nspowner))) acl
                          LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                          LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                         WHERE namespace.nspname='_timescaledb_internal'),
                    expected(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                        ({QuoteLiteral(adminRole)},{QuoteLiteral(adminRole)},'CREATE',false),
                        ({QuoteLiteral(adminRole)},{QuoteLiteral(adminRole)},'USAGE',false),
                        ('PUBLIC',{QuoteLiteral(adminRole)},'USAGE',false),
                        ({QuoteLiteral(options.Contract.TimescaleScheduler.Name)},
                         {QuoteLiteral(adminRole)},'CREATE',false))
                    SELECT EXISTS ((SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)
                                   UNION ALL
                                   (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual))) THEN
                    RAISE EXCEPTION 'transition ACL rejected' USING ERRCODE='42501';
                END IF;
                EXECUTE pg_catalog.format(
                    'REVOKE CREATE ON SCHEMA _timescaledb_internal FROM %I',
                    {QuoteLiteral(options.Contract.TimescaleScheduler.Name)});
                REVOKE USAGE ON SCHEMA _timescaledb_internal FROM PUBLIC;
                GRANT USAGE ON SCHEMA _timescaledb_internal TO {internalUsageTargets};
                IF (WITH actual AS (
                        SELECT coalesce(grantee.rolname,'PUBLIC') AS grantee,
                               grantor.rolname AS grantor,acl.privilege_type,acl.is_grantable
                          FROM pg_catalog.pg_namespace namespace
                          CROSS JOIN LATERAL pg_catalog.aclexplode(
                              coalesce(namespace.nspacl,
                                  pg_catalog.acldefault('n',namespace.nspowner))) acl
                          LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                          LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                         WHERE namespace.nspname='_timescaledb_internal'),
                    expected(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                        ({QuoteLiteral(adminRole)},{QuoteLiteral(adminRole)},'CREATE',false),
                        ({QuoteLiteral(adminRole)},{QuoteLiteral(adminRole)},'USAGE',false),
                        {terminalUsageAcl})
                    SELECT EXISTS ((SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)
                                   UNION ALL
                                   (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual))) THEN
                    RAISE EXCEPTION 'transition terminal ACL rejected' USING ERRCODE='42501';
                END IF;
                EXECUTE pg_catalog.format(
                    'ALTER FUNCTION {TransitionSchema}.{TransitionFunction}() OWNER TO %I',
                    {QuoteLiteral(options.Contract.Owner.Name)});
                EXECUTE pg_catalog.format(
                    'ALTER SCHEMA {TransitionSchema} OWNER TO %I',
                    {QuoteLiteral(options.Contract.Owner.Name)});
            END
            """;
    }

    private static async Task<bool> ScalarBoolAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params string[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private enum PrivilegePhase
    {
        Pre019,
        Post019,
    }
}

namespace Saydin.DataQualityAudit;

internal static class PrincipalRetentionAuditSql
{
    internal const string Structure = """
        WITH role_contract AS (
          SELECT owner_role,api_capability_role,timescale_scheduler_role
            FROM public.saydin_role_contract
           WHERE singleton=1 AND contract_schema_version=1
             AND database_name=pg_catalog.current_database()),
        expected_function(
          name,identity_arguments,result_type,language,volatility,strict,
          security_definer,parallel,kind,leakproof,configuration,body_sha256) AS (VALUES
          ('redact_activity_logs_before_principal_delete','','trigger','plpgsql','v',false,
           true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],
           'be2799e95d3e4abc7621598bcc116b0f8d5df0a931e4e1c5af6cb2c42cae66e6')),
        actual_function AS (
          SELECT function.proname,
                 pg_catalog.pg_get_function_identity_arguments(function.oid),
                 pg_catalog.pg_get_function_result(function.oid),language.lanname,
                 function.provolatile::text,function.proisstrict,function.prosecdef,
                 function.proparallel::text,function.prokind::text,function.proleakproof,
                 function.proconfig,
                 pg_catalog.encode(pg_catalog.sha256(
                   pg_catalog.convert_to(function.prosrc,'UTF8')),'hex')
            FROM pg_catalog.pg_proc function
            JOIN pg_catalog.pg_language language ON language.oid=function.prolang
            CROSS JOIN role_contract
           WHERE function.pronamespace='public'::pg_catalog.regnamespace
             AND function.proname='redact_activity_logs_before_principal_delete'
             AND pg_catalog.pg_get_userbyid(function.proowner)=
                 role_contract.timescale_scheduler_role),
        function_drift AS (
          (SELECT * FROM expected_function EXCEPT ALL SELECT * FROM actual_function)
          UNION ALL
          (SELECT * FROM actual_function EXCEPT ALL SELECT * FROM expected_function)),
        expected_function_acl(grantee,grantor,privilege_type,is_grantable) AS (
          SELECT timescale_scheduler_role,timescale_scheduler_role,'EXECUTE',false
            FROM role_contract),
        actual_function_acl AS (
          SELECT coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                 acl.privilege_type,acl.is_grantable
            FROM pg_catalog.pg_proc function
            CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(
              function.proacl,pg_catalog.acldefault('f',function.proowner))) acl
           LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
           LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
           WHERE function.pronamespace='public'::pg_catalog.regnamespace
             AND function.proname='redact_activity_logs_before_principal_delete'
             AND pg_catalog.pg_get_function_identity_arguments(function.oid)=''),
        function_acl_drift AS (
          (SELECT * FROM expected_function_acl EXCEPT ALL SELECT * FROM actual_function_acl)
          UNION ALL
          (SELECT * FROM actual_function_acl EXCEPT ALL SELECT * FROM expected_function_acl)),
        expected_trigger(
          relation_name,trigger_name,function_schema,function_name,
          trigger_type,enabled,arguments,condition,attributes) AS (VALUES
          ('users','trg_users_principal_retention_redact','public',
           'redact_activity_logs_before_principal_delete',11,'O',0,NULL::text,''::text)),
        actual_trigger AS (
          SELECT relation.relname,trigger.tgname,function_namespace.nspname,function.proname,
                 trigger.tgtype::integer,trigger.tgenabled::text,trigger.tgnargs,
                 trigger.tgqual::text,trigger.tgattr::text
            FROM pg_catalog.pg_trigger trigger
            JOIN pg_catalog.pg_class relation ON relation.oid=trigger.tgrelid
            JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
            JOIN pg_catalog.pg_namespace function_namespace
              ON function_namespace.oid=function.pronamespace
           WHERE trigger.tgrelid='public.users'::regclass AND NOT trigger.tgisinternal
             AND (trigger.tgname='trg_users_principal_retention_redact'
                  OR function.proname='redact_activity_logs_before_principal_delete')),
        trigger_drift AS (
          (SELECT * FROM expected_trigger EXCEPT ALL SELECT * FROM actual_trigger)
          UNION ALL
          (SELECT * FROM actual_trigger EXCEPT ALL SELECT * FROM expected_trigger)),
        expected_fk(name,relation_name,referenced_relation,kind,validated,
                    is_deferrable,is_deferred,delete_action,definition_sha256) AS (VALUES
          ('activity_logs_user_id_fkey','activity_logs','users','f',true,false,false,'a',
           '35bba6df01802e7850bd1a753b95ff643a2a01ec56aa476981cbe9dc42705cf3')),
        actual_fk AS (
          SELECT constraint_row.conname,relation.relname,referenced.relname,
                 constraint_row.contype::text,constraint_row.convalidated,
                 constraint_row.condeferrable,constraint_row.condeferred,
                 constraint_row.confdeltype::text,
                 pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                   pg_catalog.pg_get_constraintdef(constraint_row.oid,true),'UTF8')),'hex')
            FROM pg_catalog.pg_constraint constraint_row
            JOIN pg_catalog.pg_class relation ON relation.oid=constraint_row.conrelid
            JOIN pg_catalog.pg_class referenced ON referenced.oid=constraint_row.confrelid
           WHERE constraint_row.conrelid='public.activity_logs'::regclass
             AND constraint_row.conname='activity_logs_user_id_fkey'),
        fk_drift AS (
          (SELECT * FROM expected_fk EXCEPT ALL SELECT * FROM actual_fk)
          UNION ALL
          (SELECT * FROM actual_fk EXCEPT ALL SELECT * FROM expected_fk)),
        relation_set AS (
          SELECT 'public.activity_logs'::regclass AS oid
          UNION
          SELECT pg_catalog.format('%I.%I',chunk.chunk_schema,chunk.chunk_name)::regclass
            FROM timescaledb_information.chunks chunk
           WHERE chunk.hypertable_schema='public' AND chunk.hypertable_name='activity_logs'
          UNION
          SELECT relation.oid
            FROM _timescaledb_catalog.hypertable source
            JOIN _timescaledb_catalog.hypertable compressed
              ON compressed.id=source.compressed_hypertable_id
            JOIN pg_catalog.pg_namespace namespace ON namespace.nspname=compressed.schema_name
            JOIN pg_catalog.pg_class relation ON relation.relnamespace=namespace.oid
             AND relation.relname=compressed.table_name
           WHERE source.schema_name='public' AND source.table_name='activity_logs'
          UNION
          SELECT relation.oid
            FROM _timescaledb_catalog.hypertable source
            JOIN _timescaledb_catalog.chunk source_chunk ON source_chunk.hypertable_id=source.id
             AND source_chunk.compressed_chunk_id IS NOT NULL
            JOIN _timescaledb_catalog.chunk compressed_chunk
              ON compressed_chunk.id=source_chunk.compressed_chunk_id
            JOIN pg_catalog.pg_namespace namespace
              ON namespace.nspname=compressed_chunk.schema_name
            JOIN pg_catalog.pg_class relation ON relation.relnamespace=namespace.oid
             AND relation.relname=compressed_chunk.table_name
           WHERE source.schema_name='public' AND source.table_name='activity_logs'),
        relation_security_drift AS (
          SELECT relation_set.oid
            FROM relation_set
            LEFT JOIN pg_catalog.pg_class relation ON relation.oid=relation_set.oid
            CROSS JOIN role_contract
           WHERE relation.oid IS NULL
              OR pg_catalog.pg_get_userbyid(relation.relowner)<>
                 role_contract.timescale_scheduler_role
              OR relation.relrowsecurity OR relation.relforcerowsecurity),
        expected_table_acl(oid,grantee,grantor,privilege_type,is_grantable) AS (
          SELECT relation_set.oid,role_contract.api_capability_role,
                 role_contract.timescale_scheduler_role,'INSERT',false
            FROM relation_set CROSS JOIN role_contract
          UNION ALL
          SELECT relation_set.oid,role_contract.timescale_scheduler_role,
                 role_contract.timescale_scheduler_role,privilege_type,false
            FROM relation_set CROSS JOIN role_contract
            CROSS JOIN (VALUES ('SELECT'),('UPDATE')) privilege(privilege_type)
          UNION ALL
          SELECT relation_set.oid,role_contract.timescale_scheduler_role,
                 role_contract.timescale_scheduler_role,'TRIGGER',false
            FROM relation_set CROSS JOIN role_contract),
        actual_table_acl AS (
          SELECT relation.oid,coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                 acl.privilege_type,acl.is_grantable
            FROM relation_set
            JOIN pg_catalog.pg_class relation ON relation.oid=relation_set.oid
            CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) acl
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
            LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor),
        table_acl_drift AS (
          (SELECT * FROM expected_table_acl EXCEPT ALL SELECT * FROM actual_table_acl)
          UNION ALL
          (SELECT * FROM actual_table_acl EXCEPT ALL SELECT * FROM expected_table_acl)),
        column_acl_drift AS (
          SELECT relation_set.oid
            FROM relation_set
            JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid=relation_set.oid
            CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
           WHERE attribute.attnum>0 AND NOT attribute.attisdropped),
        public_create_residual AS (
          SELECT 1 FROM pg_catalog.pg_namespace namespace
          CROSS JOIN role_contract
          CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(
            namespace.nspacl,pg_catalog.acldefault('n',namespace.nspowner))) acl
          JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
         WHERE namespace.nspname IN ('public','_timescaledb_internal')
           AND grantee.rolname=role_contract.timescale_scheduler_role
           AND acl.privilege_type='CREATE'),
        compression_drift AS (
          SELECT 1 WHERE (SELECT count(*)=1 AND bool_and(compression_enabled)
                            FROM timescaledb_information.hypertables
                           WHERE hypertable_schema='public'
                             AND hypertable_name='activity_logs') IS NOT TRUE
          UNION ALL
          SELECT 1 WHERE (SELECT count(*)=1 AND bool_and(job.scheduled
                                      AND job.config->>'compress_after'='7 days')
                            FROM timescaledb_information.jobs job
                           WHERE job.hypertable_schema='public'
                             AND job.hypertable_name='activity_logs'
                             AND job.proc_name='policy_compression') IS NOT TRUE)
        SELECT (SELECT count(*)=1 FROM role_contract)
           AND NOT EXISTS(SELECT 1 FROM function_drift)
           AND NOT EXISTS(SELECT 1 FROM function_acl_drift)
           AND NOT EXISTS(SELECT 1 FROM trigger_drift)
           AND NOT EXISTS(SELECT 1 FROM fk_drift)
           AND NOT EXISTS(SELECT 1 FROM relation_security_drift)
           AND NOT EXISTS(SELECT 1 FROM table_acl_drift)
           AND NOT EXISTS(SELECT 1 FROM column_acl_drift)
           AND NOT EXISTS(SELECT 1 FROM public_create_residual)
           AND NOT pg_catalog.has_table_privilege(
                 (SELECT api_capability_role FROM role_contract),'public.users','DELETE')
           AND pg_catalog.to_regnamespace('saydin_principal_retention_control') IS NULL
           AND NOT EXISTS(SELECT 1 FROM compression_drift)
        """;
}

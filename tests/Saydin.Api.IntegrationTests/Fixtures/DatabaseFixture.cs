using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Saydin.DatabaseSecurity;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests.Fixtures;

/// <summary>
/// F2.6-21: docker-compose ağındaki gerçek PostgreSQL'e managed API login'i ve
/// owner-only password file ile bağlanır. Ayrı admin connection file yalnız fixture
/// setup/cleanup işlemlerinde kullanılır; repository SUT bu kimliği göremez.
/// Yerel optional modda erişilemezse <see cref="Available"/>=false olur ve testler
/// SkippableFact ile atlanır. Required CI modunda eksik/güvensiz hedef veya bağlantı
/// hatası fixture kurulumunu fail eder.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    private const string PriceAuthorityFingerprintSql = """
        WITH role_contract AS (
            SELECT owner_role,ingestion_capability_role
              FROM public.saydin_role_contract
             WHERE singleton=1 AND contract_schema_version=1
               AND database_name=pg_catalog.current_database()),
        expected_constraints(name,relation_name,validated,definition_sha256) AS (VALUES
            ('chk_price_points_authority_tuple','price_points',false,
             '56d37a4074f20e538a6a32bfef3dba6271160b82544b672aa2bbe12e744bf3e5'),
            ('chk_inflation_rates_authority_tuple','inflation_rates',false,
             '5cc232a700955c8093c1c7b376391000d08bbb80353a5b24fb51ffe2da8609fb')),
        actual_constraints AS (
            SELECT contract.conname,relation.relname,contract.convalidated,
                   pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                       pg_catalog.pg_get_constraintdef(contract.oid,true),'UTF8')),'hex')
              FROM expected_constraints expected
              LEFT JOIN pg_catalog.pg_constraint contract
                ON contract.connamespace='public'::pg_catalog.regnamespace
               AND contract.conname=expected.name
              LEFT JOIN pg_catalog.pg_class relation ON relation.oid=contract.conrelid),
        constraint_differences AS (
            (SELECT * FROM expected_constraints EXCEPT ALL SELECT * FROM actual_constraints)
            UNION ALL
            (SELECT * FROM actual_constraints EXCEPT ALL SELECT * FROM expected_constraints)),
        expected_functions(name,identity_arguments,result_type,strict,language,volatility,body_sha256) AS (VALUES
            ('saydin_source_raw_allowed','payload jsonb','boolean',true,'sql','i',
             'b656a6a3ccbe9c0e7172fba6738697f98d68de708d263b1ad25fa73237113d07'),
            ('saydin_canonical_observation','payload jsonb','jsonb',true,'sql','i',
             '33535c05ce918127ab5c98fe0bb4bc90082dbe8f2bb881c61a2a45879869a04a'),
            ('enforce_price_point_authority','','trigger',false,'plpgsql','v',
             '7705a66f958768e4e070fc271569084d0e2bc6b87d145b82609138910d5e9ac4'),
            ('enforce_inflation_rate_authority','','trigger',false,'plpgsql','v',
             '2a7f5fc9469f5e13f3f5f776561030b17d60b8f72ff2fd0d2fadf12139764232'),
            ('enforce_observation_attribution','','trigger',false,'plpgsql','v',
             '1097075efe80dd06651f8911d7cc0a7b99ed028de00888495d89997f04e5bb3b'),
            ('enforce_fetch_payload_insert','','trigger',false,'plpgsql','v',
             '60c2b368883fb285ea9769c7af8c31be81417151425839375e78243311375ce4'),
            ('reject_fetch_payload_mutation','','trigger',false,'plpgsql','v',
             '50e1f311966cc9298ad4d41986c552526d4bfd911527d93db85da59acf71eaf4')),
        actual_functions AS (
            SELECT expected.name,expected.identity_arguments,expected.result_type,
                   expected.strict,expected.language,expected.volatility,expected.body_sha256,
                   function.oid,function.proowner,function.proacl
              FROM expected_functions expected
              LEFT JOIN pg_catalog.pg_proc function
                ON function.pronamespace='public'::pg_catalog.regnamespace
               AND function.proname=expected.name
              LEFT JOIN pg_catalog.pg_language language ON language.oid=function.prolang
             WHERE function.oid IS NOT NULL
               AND pg_catalog.pg_get_userbyid(function.proowner)=
                   (SELECT owner_role FROM role_contract)
               AND pg_catalog.pg_get_function_identity_arguments(function.oid)=expected.identity_arguments
               AND pg_catalog.pg_get_function_result(function.oid)=expected.result_type
               AND function.proisstrict=expected.strict AND function.prokind='f'
               AND language.lanname=expected.language
               AND function.provolatile=expected.volatility
               AND function.proparallel='u' AND NOT function.proleakproof
               AND NOT function.prosecdef
               AND function.proconfig=ARRAY['search_path=pg_catalog, pg_temp']::text[]
               AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                   function.prosrc,'UTF8')),'hex')=expected.body_sha256),
        expected_function_acl(signature,grantee,grantor,privilege_type,is_grantable) AS (VALUES
            ('saydin_source_raw_allowed(jsonb)',
             (SELECT ingestion_capability_role FROM role_contract),
             (SELECT owner_role FROM role_contract),'EXECUTE',false),
            ('saydin_canonical_observation(jsonb)',
             (SELECT ingestion_capability_role FROM role_contract),
             (SELECT owner_role FROM role_contract),'EXECUTE',false)),
        actual_function_acl AS (
            SELECT function.proname||'('||pg_catalog.oidvectortypes(function.proargtypes)||')',
                   CASE WHEN grantee.rolname IS NULL THEN 'PUBLIC' ELSE grantee.rolname END,
                   grantor.rolname,
                   acl.privilege_type,acl.is_grantable
              FROM actual_functions actual
              JOIN pg_catalog.pg_proc function ON function.oid=actual.oid
              CROSS JOIN LATERAL pg_catalog.aclexplode(
                  CASE WHEN function.proacl IS NULL
                       THEN pg_catalog.acldefault('f',function.proowner)
                       ELSE function.proacl
                   END) acl
              LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
              LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
             WHERE acl.grantee<>function.proowner),
        function_acl_differences AS (
            (SELECT * FROM expected_function_acl EXCEPT ALL SELECT * FROM actual_function_acl)
            UNION ALL
            (SELECT * FROM actual_function_acl EXCEPT ALL SELECT * FROM expected_function_acl)),
        expected_triggers(relation_name,trigger_name,function_schema,function_name,trigger_type,enabled) AS (VALUES
            ('price_points','ts_insert_blocker','_timescaledb_functions','insert_blocker',7,'O'),
            ('price_points','trg_price_points_ingestion_fence','public','enforce_price_point_ingestion_fence',23,'O'),
            ('price_points','trg_price_points_authority','public','enforce_price_point_authority',23,'O'),
            ('inflation_rates','trg_inflation_rates_ingestion_fence','public','enforce_inflation_rate_ingestion_fence',23,'A'),
            ('inflation_rates','trg_inflation_rates_authority','public','enforce_inflation_rate_authority',23,'A'),
            ('price_observation_attributions','trg_price_attribution_append_only','public','enforce_observation_attribution',31,'O'),
            ('price_observation_attributions','trg_price_attribution_no_truncate','public','reject_fetch_payload_mutation',34,'O'),
            ('inflation_observation_attributions','trg_inflation_attribution_append_only','public','enforce_observation_attribution',31,'O'),
            ('inflation_observation_attributions','trg_inflation_attribution_no_truncate','public','reject_fetch_payload_mutation',34,'O'),
            ('provider_fetch_payloads','trg_fetch_payload_append_only','public','reject_fetch_payload_mutation',27,'O'),
            ('provider_fetch_payloads','trg_fetch_payload_live_lease','public','enforce_fetch_payload_insert',7,'O'),
            ('provider_fetch_payloads','trg_fetch_payload_no_truncate','public','reject_fetch_payload_mutation',34,'O')),
        actual_triggers AS (
            SELECT relation.relname,trigger.tgname,function_namespace.nspname,function.proname,
                   trigger.tgtype::integer,trigger.tgenabled::text
              FROM pg_catalog.pg_class relation
              JOIN pg_catalog.pg_trigger trigger
                ON trigger.tgrelid=relation.oid AND NOT trigger.tgisinternal
              JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
              JOIN pg_catalog.pg_namespace function_namespace
                ON function_namespace.oid=function.pronamespace
             WHERE relation.relnamespace='public'::pg_catalog.regnamespace
               AND relation.relname IN (SELECT DISTINCT relation_name FROM expected_triggers)),
        trigger_differences AS (
            (SELECT * FROM expected_triggers EXCEPT ALL SELECT * FROM actual_triggers)
            UNION ALL
            (SELECT * FROM actual_triggers EXCEPT ALL SELECT * FROM expected_triggers))
        SELECT (SELECT count(*)=1 FROM role_contract)
           AND EXISTS (
               SELECT 1 FROM public.schema_migrations
                WHERE version='020_price_authority_expand'
                  AND checksum='8cb3f07bffef6013f42d196a20f0c08ed3e02547028d5694d6fba5f9749c52a8'
                  AND state='succeeded')
           AND NOT EXISTS (SELECT 1 FROM constraint_differences)
           AND (SELECT count(*) FROM actual_functions)=7
           AND NOT EXISTS (SELECT 1 FROM function_acl_differences)
           AND NOT EXISTS (SELECT 1 FROM trigger_differences)
        """;

    private const string PriceAuthorityStructureFingerprintSql = """
        WITH expected_columns(relation_name,column_name,type_name,not_null,default_expression) AS (VALUES
            ('price_points','provider_source','character varying(32)',false,NULL),
            ('price_points','source_observation_id','character varying(256)',false,NULL),
            ('price_points','as_of_at','timestamp with time zone',false,NULL),
            ('price_points','price_kind','character varying(32)',false,NULL),
            ('price_points','is_final','boolean',false,NULL),
            ('price_points','observation_sha256','bytea',false,NULL),
            ('price_points','authority_contract_version','integer',false,NULL),
            ('inflation_rates','provider_source','character varying(32)',false,NULL),
            ('inflation_rates','source_observation_id','character varying(256)',false,NULL),
            ('inflation_rates','as_of_at','timestamp with time zone',false,NULL),
            ('inflation_rates','price_kind','character varying(32)',false,NULL),
            ('inflation_rates','is_final','boolean',false,NULL),
            ('inflation_rates','observation_sha256','bytea',false,NULL),
            ('inflation_rates','authority_contract_version','integer',false,NULL),
            ('inflation_rates','source_raw','jsonb',false,NULL),
            ('provider_fetch_payloads','provider_source','character varying(32)',true,NULL),
            ('provider_fetch_payloads','payload_sha256','bytea',true,NULL),
            ('provider_fetch_payloads','payload_byte_length','integer',true,NULL),
            ('provider_fetch_payloads','first_observed_at','timestamp with time zone',true,'clock_timestamp()'),
            ('price_observation_attributions','asset_id','uuid',true,NULL),
            ('price_observation_attributions','price_date','date',true,NULL),
            ('price_observation_attributions','ingestion_window_id','uuid',true,NULL),
            ('price_observation_attributions','provider_source','character varying(32)',true,NULL),
            ('price_observation_attributions','payload_sha256','bytea',true,NULL),
            ('price_observation_attributions','source_observation_id','character varying(256)',true,NULL),
            ('price_observation_attributions','observation_sha256','bytea',true,NULL),
            ('price_observation_attributions','authority_contract_version','integer',true,NULL),
            ('price_observation_attributions','attributed_at','timestamp with time zone',true,'clock_timestamp()'),
            ('inflation_observation_attributions','period_date','date',true,NULL),
            ('inflation_observation_attributions','source','character varying(20)',true,NULL),
            ('inflation_observation_attributions','ingestion_window_id','uuid',true,NULL),
            ('inflation_observation_attributions','provider_source','character varying(32)',true,NULL),
            ('inflation_observation_attributions','payload_sha256','bytea',true,NULL),
            ('inflation_observation_attributions','source_observation_id','character varying(256)',true,NULL),
            ('inflation_observation_attributions','observation_sha256','bytea',true,NULL),
            ('inflation_observation_attributions','authority_contract_version','integer',true,NULL),
            ('inflation_observation_attributions','attributed_at','timestamp with time zone',true,'clock_timestamp()')),
        actual_columns AS (
            SELECT relation.relname,attribute.attname,
                   pg_catalog.format_type(attribute.atttypid,attribute.atttypmod),
                   attribute.attnotnull,
                   pg_catalog.pg_get_expr(default_value.adbin,default_value.adrelid)
              FROM expected_columns expected
              LEFT JOIN pg_catalog.pg_class relation
                ON relation.relnamespace='public'::pg_catalog.regnamespace
               AND relation.relname=expected.relation_name
              LEFT JOIN pg_catalog.pg_attribute attribute
                ON attribute.attrelid=relation.oid AND attribute.attname=expected.column_name
               AND attribute.attnum>0 AND NOT attribute.attisdropped
              LEFT JOIN pg_catalog.pg_attrdef default_value
                ON default_value.adrelid=relation.oid AND default_value.adnum=attribute.attnum),
        column_differences AS (
            (SELECT * FROM expected_columns EXCEPT ALL SELECT * FROM actual_columns)
            UNION ALL
            (SELECT * FROM actual_columns EXCEPT ALL SELECT * FROM expected_columns)),
        expected_constraints(name,relation_name,kind,validated,delete_action,definition_sha256) AS (VALUES
            ('chk_price_points_authority_tuple','price_points','c',false,NULL,'56d37a4074f20e538a6a32bfef3dba6271160b82544b672aa2bbe12e744bf3e5'),
            ('chk_price_points_provider_kind','price_points','c',false,NULL,'15dbb9012ff5cb5411e43c4abad1790214399ed5dad677715f0c2b8350feae5a'),
            ('chk_price_points_numeric','price_points','c',false,NULL,'dca4038f346c80c13292b92135b6f1640b68a57ba2589e4925a7d26b5e556f57'),
            ('chk_price_points_provider_shape','price_points','c',false,NULL,'56e7e2dfd5a083c5cb3eb14e1765b6bf7256e1d2a16d1705b52f82f2b94d6af0'),
            ('chk_price_points_as_of','price_points','c',false,NULL,'c9191bfeb8179e38f6376eeb79817d89d5d69e99c755d86680fd83122c434268'),
            ('chk_inflation_rates_authority_tuple','inflation_rates','c',false,NULL,'5cc232a700955c8093c1c7b376391000d08bbb80353a5b24fb51ffe2da8609fb'),
            ('chk_inflation_rates_numeric','inflation_rates','c',false,NULL,'995968bcc5179b2e8f517449d3f678d995e6404ed6eb2bbafe4414ef21cf8291'),
            ('chk_inflation_rates_as_of','inflation_rates','c',false,NULL,'6550f93376e8d144fadd75f8a7a3c2d647c2e530950ef6e3192c9cecf97b4b09'),
            ('pk_provider_fetch_payloads','provider_fetch_payloads','p',true,NULL,'ae4dce90881dda440e15c9840a665698f9fed0011b58c994ffc4ef63b9d45e2e'),
            ('chk_provider_fetch_payloads_source','provider_fetch_payloads','c',true,NULL,'1a8d522a3b16a2e9fd38450274f6fe071ddf01d2c6edb8b7c071817c6f95bb75'),
            ('chk_provider_fetch_payloads_sha','provider_fetch_payloads','c',true,NULL,'6cdfef74f1ab94b20d2db88df8cb88922d7cd7008cb5150b88aa87a7e7acba9e'),
            ('chk_provider_fetch_payloads_length','provider_fetch_payloads','c',true,NULL,'d77ba96b6b4c91f0831468e8c3cf3b5297f204c7221282a417a95d5425749fd3'),
            ('pk_price_observation_attributions','price_observation_attributions','p',true,NULL,'a30023c43fe95b7621c55f48c10ec2760357c3d6f5c00055f075d41eaefcd86e'),
            ('fk_price_attribution_window','price_observation_attributions','f',true,'r','299d64815f6570c929dc5a64fe99e4767fba85245c79e9359b8c4459e80986fb'),
            ('fk_price_attribution_payload','price_observation_attributions','f',true,'r','4daaa036b74238e40841fb481de72012e80fbeec6e337407896cdcd8b2b147f2'),
            ('chk_price_attribution_sha','price_observation_attributions','c',true,NULL,'e823465c0b70501b8ad6632cf6f7295d745a6dc22114f30cd53f04f57a9181f6'),
            ('chk_price_attribution_contract','price_observation_attributions','c',true,NULL,'a0a9e709a8141cb987cb991ea9109dc01947d7b3e065872ac4d3a2e92d41c8e0'),
            ('pk_inflation_observation_attributions','inflation_observation_attributions','p',true,NULL,'d437b49347be7b3a384d39a06afc0e019e9cf0205c01c1d65119a3bcb3f2f928'),
            ('fk_inflation_attribution_observation','inflation_observation_attributions','f',true,'r','80cd8c0196e1ab6b7cac97e777f6e309c5dd2cef5d3a50d21c03a414ac90a665'),
            ('fk_inflation_attribution_window','inflation_observation_attributions','f',true,'r','299d64815f6570c929dc5a64fe99e4767fba85245c79e9359b8c4459e80986fb'),
            ('fk_inflation_attribution_payload','inflation_observation_attributions','f',true,'r','4daaa036b74238e40841fb481de72012e80fbeec6e337407896cdcd8b2b147f2'),
            ('chk_inflation_attribution_sha','inflation_observation_attributions','c',true,NULL,'e823465c0b70501b8ad6632cf6f7295d745a6dc22114f30cd53f04f57a9181f6'),
            ('chk_inflation_attribution_contract','inflation_observation_attributions','c',true,NULL,'a0a9e709a8141cb987cb991ea9109dc01947d7b3e065872ac4d3a2e92d41c8e0')),
        actual_constraints AS (
            SELECT contract.conname,relation.relname,contract.contype::text,
                   contract.convalidated,
                   CASE WHEN contract.contype='f' THEN contract.confdeltype::text END,
                   pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                       pg_catalog.pg_get_constraintdef(contract.oid,true),'UTF8')),'hex')
              FROM expected_constraints expected
              LEFT JOIN pg_catalog.pg_constraint contract
                ON contract.connamespace='public'::pg_catalog.regnamespace
               AND contract.conname=expected.name
              LEFT JOIN pg_catalog.pg_class relation ON relation.oid=contract.conrelid),
        constraint_differences AS (
            (SELECT * FROM expected_constraints EXCEPT ALL SELECT * FROM actual_constraints)
            UNION ALL
            (SELECT * FROM actual_constraints EXCEPT ALL SELECT * FROM expected_constraints))
        SELECT NOT EXISTS (SELECT 1 FROM column_differences)
           AND NOT EXISTS (SELECT 1 FROM constraint_differences)
           AND NOT EXISTS (
               SELECT 1 FROM pg_catalog.pg_index index
               JOIN pg_catalog.pg_class relation ON relation.oid=index.indrelid
               JOIN pg_catalog.pg_class index_relation ON index_relation.oid=index.indexrelid
              WHERE relation.relnamespace='public'::pg_catalog.regnamespace
                AND relation.relname IN ('provider_fetch_payloads','price_observation_attributions',
                                         'inflation_observation_attributions')
                AND (NOT index.indisprimary OR NOT index.indisunique OR NOT index.indisvalid
                     OR NOT index.indisready OR index_relation.relname NOT LIKE 'pk_%'))
           AND (SELECT count(*) FROM pg_catalog.pg_index index
                JOIN pg_catalog.pg_class relation ON relation.oid=index.indrelid
               WHERE relation.relnamespace='public'::pg_catalog.regnamespace
                 AND relation.relname IN ('provider_fetch_payloads','price_observation_attributions',
                                          'inflation_observation_attributions'))=3
        """;

    private const string PriceAuthorityAclFingerprintSql = """
        WITH role_contract AS (
            SELECT owner_role,api_capability_role,ingestion_capability_role,
                   audit_capability_role
              FROM public.saydin_role_contract
             WHERE singleton=1 AND contract_schema_version=1
               AND database_name=pg_catalog.current_database()),
        relations(name) AS (VALUES
            ('price_points'),('inflation_rates'),('provider_fetch_payloads'),
            ('price_observation_attributions'),('inflation_observation_attributions')),
        expected_table_acl(relation_name,grantee,grantor,privilege_type,is_grantable) AS (
            SELECT relation_name,grantee,
                   (SELECT owner_role FROM role_contract),privilege_type,false
              FROM (VALUES
                  ('price_points',(SELECT api_capability_role FROM role_contract),'SELECT'),
                  ('price_points',(SELECT ingestion_capability_role FROM role_contract),'SELECT'),
                  ('price_points',(SELECT audit_capability_role FROM role_contract),'SELECT'),
                  ('inflation_rates',(SELECT api_capability_role FROM role_contract),'SELECT'),
                  ('inflation_rates',(SELECT ingestion_capability_role FROM role_contract),'SELECT'),
                  ('inflation_rates',(SELECT audit_capability_role FROM role_contract),'SELECT'),
                  ('provider_fetch_payloads',(SELECT ingestion_capability_role FROM role_contract),'SELECT'),
                  ('provider_fetch_payloads',(SELECT audit_capability_role FROM role_contract),'SELECT'),
                  ('price_observation_attributions',(SELECT ingestion_capability_role FROM role_contract),'SELECT'),
                  ('price_observation_attributions',(SELECT audit_capability_role FROM role_contract),'SELECT'),
                  ('inflation_observation_attributions',(SELECT ingestion_capability_role FROM role_contract),'SELECT'),
                  ('inflation_observation_attributions',(SELECT audit_capability_role FROM role_contract),'SELECT')
              ) grants(relation_name,grantee,privilege_type)),
        actual_table_acl AS (
            SELECT relation.relname,grantee.rolname,grantor.rolname,
                   acl.privilege_type,acl.is_grantable
              FROM relations
              JOIN pg_catalog.pg_class relation
                ON relation.relnamespace='public'::pg_catalog.regnamespace
               AND relation.relname=relations.name
              CROSS JOIN LATERAL pg_catalog.aclexplode(
                  COALESCE(relation.relacl,
                      pg_catalog.acldefault('r',relation.relowner))) acl
              LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
              LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
             WHERE acl.grantee<>relation.relowner),
        table_acl_differences AS (
            (SELECT * FROM expected_table_acl EXCEPT ALL SELECT * FROM actual_table_acl)
            UNION ALL
            (SELECT * FROM actual_table_acl EXCEPT ALL SELECT * FROM expected_table_acl)),
        expected_column_acl(relation_name,column_name,grantee,grantor,privilege_type,is_grantable) AS (
            SELECT relation_name,column_name,
                   (SELECT ingestion_capability_role FROM role_contract),
                   (SELECT owner_role FROM role_contract),privilege_type,false
              FROM (VALUES
                  ('price_points','asset_id','INSERT'),
                  ('price_points','price_date','INSERT'),
                  ('price_points','close','INSERT'),('price_points','close','UPDATE'),
                  ('price_points','open','INSERT'),('price_points','open','UPDATE'),
                  ('price_points','high','INSERT'),('price_points','high','UPDATE'),
                  ('price_points','low','INSERT'),('price_points','low','UPDATE'),
                  ('price_points','volume','INSERT'),('price_points','volume','UPDATE'),
                  ('price_points','provider_source','INSERT'),('price_points','provider_source','UPDATE'),
                  ('price_points','source_observation_id','INSERT'),('price_points','source_observation_id','UPDATE'),
                  ('price_points','as_of_at','INSERT'),('price_points','as_of_at','UPDATE'),
                  ('price_points','price_kind','INSERT'),('price_points','price_kind','UPDATE'),
                  ('price_points','is_final','INSERT'),('price_points','is_final','UPDATE'),
                  ('price_points','observation_sha256','INSERT'),('price_points','observation_sha256','UPDATE'),
                  ('price_points','authority_contract_version','INSERT'),('price_points','authority_contract_version','UPDATE'),
                  ('price_points','source_raw','INSERT'),('price_points','source_raw','UPDATE'),
                  ('inflation_rates','period_date','INSERT'),
                  ('inflation_rates','index_value','INSERT'),('inflation_rates','index_value','UPDATE'),
                  ('inflation_rates','source','INSERT'),
                  ('inflation_rates','provider_source','INSERT'),('inflation_rates','provider_source','UPDATE'),
                  ('inflation_rates','source_observation_id','INSERT'),('inflation_rates','source_observation_id','UPDATE'),
                  ('inflation_rates','as_of_at','INSERT'),('inflation_rates','as_of_at','UPDATE'),
                  ('inflation_rates','price_kind','INSERT'),('inflation_rates','price_kind','UPDATE'),
                  ('inflation_rates','is_final','INSERT'),('inflation_rates','is_final','UPDATE'),
                  ('inflation_rates','observation_sha256','INSERT'),('inflation_rates','observation_sha256','UPDATE'),
                  ('inflation_rates','authority_contract_version','INSERT'),('inflation_rates','authority_contract_version','UPDATE'),
                  ('inflation_rates','source_raw','INSERT'),('inflation_rates','source_raw','UPDATE'),
                  ('provider_fetch_payloads','provider_source','INSERT'),
                  ('provider_fetch_payloads','payload_sha256','INSERT'),
                  ('provider_fetch_payloads','payload_byte_length','INSERT'),
                  ('price_observation_attributions','asset_id','INSERT'),
                  ('price_observation_attributions','price_date','INSERT'),
                  ('price_observation_attributions','ingestion_window_id','INSERT'),
                  ('price_observation_attributions','provider_source','INSERT'),
                  ('price_observation_attributions','payload_sha256','INSERT'),
                  ('price_observation_attributions','source_observation_id','INSERT'),
                  ('price_observation_attributions','observation_sha256','INSERT'),
                  ('price_observation_attributions','authority_contract_version','INSERT'),
                  ('inflation_observation_attributions','period_date','INSERT'),
                  ('inflation_observation_attributions','source','INSERT'),
                  ('inflation_observation_attributions','ingestion_window_id','INSERT'),
                  ('inflation_observation_attributions','provider_source','INSERT'),
                  ('inflation_observation_attributions','payload_sha256','INSERT'),
                  ('inflation_observation_attributions','source_observation_id','INSERT'),
                  ('inflation_observation_attributions','observation_sha256','INSERT'),
                  ('inflation_observation_attributions','authority_contract_version','INSERT')
              ) grants(relation_name,column_name,privilege_type)),
        actual_column_acl AS (
            SELECT relation.relname,attribute.attname,grantee.rolname,grantor.rolname,
                   acl.privilege_type,acl.is_grantable
              FROM relations
              JOIN pg_catalog.pg_class relation
                ON relation.relnamespace='public'::pg_catalog.regnamespace
               AND relation.relname=relations.name
              JOIN pg_catalog.pg_attribute attribute
                ON attribute.attrelid=relation.oid AND attribute.attnum>0
               AND NOT attribute.attisdropped
              CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
              LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
              LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
             WHERE acl.grantee<>relation.relowner),
        column_acl_differences AS (
            (SELECT * FROM expected_column_acl EXCEPT ALL SELECT * FROM actual_column_acl)
            UNION ALL
            (SELECT * FROM actual_column_acl EXCEPT ALL SELECT * FROM expected_column_acl))
        SELECT (SELECT count(*)=1 FROM role_contract)
           AND (SELECT count(*)=5 AND count(relation.oid)=5
                  FROM relations
                  LEFT JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace='public'::pg_catalog.regnamespace
                   AND relation.relname=relations.name
                 WHERE relation.relkind IN ('r','p') AND relation.relpersistence='p'
                   AND pg_catalog.pg_get_userbyid(relation.relowner)=
                       (SELECT owner_role FROM role_contract)
                   AND NOT relation.relrowsecurity AND NOT relation.relforcerowsecurity
                   AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy
                                    WHERE policy.polrelid=relation.oid))
           AND NOT EXISTS (SELECT 1 FROM table_acl_differences)
           AND NOT EXISTS (SELECT 1 FROM column_acl_differences)
        """;

    private const string PriceAuthorityChunkFingerprintSql = """
        WITH role_contract AS (
            SELECT owner_role,api_capability_role,ingestion_capability_role,
                   audit_capability_role
              FROM public.saydin_role_contract
             WHERE singleton=1 AND contract_schema_version=1
               AND database_name=pg_catalog.current_database()),
        writable_chunks AS (
            SELECT chunks.chunk_schema AS schema_name,chunks.chunk_name AS table_name
              FROM timescaledb_information.chunks chunks
             WHERE chunks.hypertable_schema='public'
               AND chunks.hypertable_name='price_points'),
        all_chunks AS (
            SELECT schema_name,table_name FROM writable_chunks
            UNION
            SELECT compressed.schema_name,compressed.table_name
              FROM _timescaledb_catalog.chunk source
              JOIN _timescaledb_catalog.chunk compressed
                ON compressed.id=source.compressed_chunk_id
              JOIN _timescaledb_catalog.hypertable hypertable
                ON hypertable.id=source.hypertable_id
             WHERE hypertable.schema_name='public'
               AND hypertable.table_name='price_points'
               AND NOT compressed.dropped),
        relations AS (
            SELECT all_chunks.*,relation.oid,relation.relowner,relation.relacl,
                   relation.relrowsecurity,relation.relforcerowsecurity
              FROM all_chunks
              LEFT JOIN pg_catalog.pg_namespace namespace
                ON namespace.nspname=all_chunks.schema_name
              LEFT JOIN pg_catalog.pg_class relation
                ON relation.relnamespace=namespace.oid
               AND relation.relname=all_chunks.table_name),
        expected_acl AS (
            SELECT relations.schema_name,relations.table_name,grant_row.grantee,
                   grant_row.privilege_type,
                   (SELECT owner_role FROM role_contract) AS grantor,false AS is_grantable
              FROM relations
              CROSS JOIN LATERAL (VALUES
                  ((SELECT api_capability_role FROM role_contract),'SELECT'),
                  ((SELECT ingestion_capability_role FROM role_contract),'SELECT'),
                  ((SELECT audit_capability_role FROM role_contract),'SELECT'))
                  grant_row(grantee,privilege_type)),
        actual_acl AS (
            SELECT relations.schema_name,relations.table_name,grantee.rolname,
                   acl.privilege_type,grantor.rolname,acl.is_grantable
              FROM relations
              CROSS JOIN LATERAL pg_catalog.aclexplode(
                  COALESCE(relations.relacl,
                      pg_catalog.acldefault('r',relations.relowner))) acl
              LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
              LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
             WHERE acl.grantee<>relations.relowner),
        acl_differences AS (
            (SELECT * FROM expected_acl EXCEPT ALL SELECT * FROM actual_acl)
            UNION ALL
            (SELECT * FROM actual_acl EXCEPT ALL SELECT * FROM expected_acl)),
        writable_relations AS (
            SELECT writable_chunks.*,relation.oid
              FROM writable_chunks
              LEFT JOIN pg_catalog.pg_namespace namespace
                ON namespace.nspname=writable_chunks.schema_name
              LEFT JOIN pg_catalog.pg_class relation
                ON relation.relnamespace=namespace.oid
               AND relation.relname=writable_chunks.table_name),
        expected_trigger AS (VALUES
            ('trg_price_points_authority','public','enforce_price_point_authority',23,'O'),
            ('trg_price_points_ingestion_fence','public','enforce_price_point_ingestion_fence',23,'O')),
        expected_trigger_by_chunk AS (
            SELECT writable_relations.oid,expected_trigger.*
              FROM writable_relations CROSS JOIN expected_trigger),
        actual_trigger AS (
            SELECT writable_relations.oid,trigger.tgname,function_namespace.nspname,
                   function.proname,trigger.tgtype::integer,trigger.tgenabled::text
              FROM writable_relations
              JOIN pg_catalog.pg_trigger trigger
                ON trigger.tgrelid=writable_relations.oid AND NOT trigger.tgisinternal
              JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
              JOIN pg_catalog.pg_namespace function_namespace
                ON function_namespace.oid=function.pronamespace),
        trigger_differences AS (
            (SELECT * FROM expected_trigger_by_chunk EXCEPT ALL SELECT * FROM actual_trigger)
            UNION ALL
            (SELECT * FROM actual_trigger EXCEPT ALL SELECT * FROM expected_trigger_by_chunk))
        SELECT (SELECT count(*)=1 FROM role_contract)
           AND (SELECT count(*)=count(oid) FROM writable_relations)
           AND NOT EXISTS (
               SELECT 1 FROM relations
                WHERE oid IS NULL
                   OR pg_catalog.pg_get_userbyid(relowner)<>
                      (SELECT owner_role FROM role_contract)
                   OR relrowsecurity OR relforcerowsecurity
                   OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy
                               WHERE policy.polrelid=relations.oid))
           AND NOT EXISTS (SELECT 1 FROM acl_differences)
           AND NOT EXISTS (
               SELECT 1
                 FROM relations
                 JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid=relations.oid
                 CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
                WHERE attribute.attnum>0 AND NOT attribute.attisdropped)
           AND NOT EXISTS (SELECT 1 FROM trigger_differences)
        """;

    private readonly NpgsqlDataSource? _dataSource;
    private readonly DbContextOptions<SaydinDbContext>? _options;
    private readonly DbContextOptions<SaydinDbContext>? _adminOptions;

    public bool Available { get; }
    public string SkipReason { get; } = string.Empty;

    /// <summary>
    /// Migration 012 (F2.7-5) uygulanmış mı — inflation_rates PK composite (period_date, source) mı?
    /// Eski şemalı (henüz migrate edilmemiş) bir DB'de inflation testi spurious fail etmesin diye
    /// test bu bayrakla skip eder.
    /// </summary>
    public bool CompositeInflationPk { get; }
    public bool IngestionWriteFence { get; }
    public bool ScenarioIntegrity { get; }
    public bool PriceAuthority { get; }
    public string ConnectionString { get; } = string.Empty;

    public DatabaseFixture()
    {
        var required = IntegrationTestEnvironment.IsRequired;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGHOST")))
        {
            if (required)
                throw new InvalidOperationException(
                    "Required integration modunda managed PostgreSQL topology env zorunludur.");

            SkipReason = "Managed PostgreSQL topology env yok (entegrasyon DB'si erişilemez).";
            return;
        }

        try
        {
            var runtime = RuntimeDatabaseOptions.FromEnvironment(
                LoginPurpose.Api, RuntimeDatabasePooling.Service);
            IntegrationTestEnvironment.ValidateRequiredDatabase(runtime.Host, runtime.Database);
            _dataSource = RuntimeDatabase.OpenVerifiedDataSourceAsync(
                    runtime, builder => builder.MapEnum<AssetCategory>("asset_category"))
                .GetAwaiter().GetResult();

            var adminConnection = SecureSecretFile.ReadConnectionString(
                Environment.GetEnvironmentVariable("SAYDIN_TEST_ADMIN_CONNECTION_FILE") ??
                throw new InvalidOperationException("Admin setup connection file missing."));
            _adminOptions = new DbContextOptionsBuilder<SaydinDbContext>()
                .UseNpgsql(adminConnection, npgsql => npgsql.MapEnum<AssetCategory>("asset_category"))
                .UseSnakeCaseNamingConvention()
                .Options;

            // Hızlı erişilebilirlik kontrolü + migration 012 şema probu.
            using (var conn = _dataSource.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'pk_inflation_rates'";
                var pkDef = cmd.ExecuteScalar() as string ?? string.Empty;
                CompositeInflationPk = pkDef.Contains("source", StringComparison.OrdinalIgnoreCase);

                cmd.CommandText = """
                    SELECT to_regprocedure('public.enforce_inflation_rate_ingestion_fence()') IS NOT NULL
                       AND EXISTS (SELECT 1 FROM pg_trigger
                                   WHERE tgname='trg_inflation_rates_ingestion_fence'
                                     AND tgenabled='A')
                    """;
                IngestionWriteFence = cmd.ExecuteScalar() is true;

                cmd.CommandText = """
                    SELECT COUNT(*) = 3
                       AND bool_and(convalidated)
                      FROM pg_constraint
                     WHERE conrelid='public.saved_scenarios'::regclass
                       AND conname IN ('chk_saved_scenarios_extra_data_object',
                                      'chk_saved_scenarios_extra_data_size',
                                      'chk_saved_scenarios_type_unit')
                       AND EXISTS (
                           SELECT 1 FROM pg_indexes
                            WHERE schemaname='public'
                              AND indexname='idx_saved_scenarios_user_created_id_desc')
                       AND EXISTS (
                           SELECT 1 FROM pg_trigger
                            WHERE tgrelid='public.saved_scenarios'::regclass
                              AND tgname='trg_saved_scenarios_hard_cap'
                              AND tgenabled='O' AND NOT tgisinternal)
                    """;
                ScenarioIntegrity = cmd.ExecuteScalar() is true;

                cmd.CommandText = """
                    SELECT (SELECT COUNT(*)=2 FROM pg_catalog.pg_trigger
                             WHERE (tgrelid,tgname,tgenabled) IN (
                                 ('public.price_points'::regclass,'trg_price_points_authority','O'),
                                 ('public.inflation_rates'::regclass,'trg_inflation_rates_authority','A'))
                             AND NOT tgisinternal)
                       AND to_regprocedure('public.saydin_canonical_observation(jsonb)') IS NOT NULL
                       AND to_regclass('public.provider_fetch_payloads') IS NOT NULL
                       AND to_regclass('public.price_observation_attributions') IS NOT NULL
                       AND to_regclass('public.inflation_observation_attributions') IS NOT NULL
                       AND EXISTS (
                           SELECT 1 FROM pg_catalog.pg_constraint
                            WHERE conrelid='public.price_points'::regclass
                              AND conname='chk_price_points_authority_tuple')
                       AND EXISTS (
                           SELECT 1 FROM pg_catalog.pg_constraint
                            WHERE conrelid='public.inflation_rates'::regclass
                              AND conname='chk_inflation_rates_authority_tuple')
                    """;
                PriceAuthority = cmd.ExecuteScalar() is true;
            }

            // schema_migrations is intentionally not readable by the managed API login. The fixture
            // validates the frozen migration row through its setup-only admin identity, while every
            // SUT repository/HTTP query above and below remains on the managed API datasource.
            using (var admin = new NpgsqlConnection(adminConnection))
            {
                admin.Open();
                PriceAuthority = PriceAuthority
                    && VerifyPriceAuthorityFingerprint(admin);
            }

            if (required && !CompositeInflationPk)
                throw new InvalidOperationException(
                    "Required integration DB'sinde migration 012 composite inflation PK doğrulanamadı.");
            if (required && !IngestionWriteFence)
                throw new InvalidOperationException(
                    "Required integration DB'sinde migration 016 ingestion writer fence doğrulanamadı.");
            if (required && !ScenarioIntegrity)
                throw new InvalidOperationException(
                    "Required integration DB'sinde migration 018 scenario integrity doğrulanamadı.");
            if (required && !PriceAuthority)
                throw new InvalidOperationException(
                    "Required integration DB'sinde frozen migration 020 authority fingerprint doğrulanamadı.");

            _options = new DbContextOptionsBuilder<SaydinDbContext>()
                .UseNpgsql(_dataSource, npgsql => npgsql.MapEnum<AssetCategory>("asset_category"))
                .UseSnakeCaseNamingConvention()
                .Options;

            ConnectionString = new NpgsqlConnectionStringBuilder
            {
                Host = runtime.Host,
                Port = runtime.Port,
                Database = runtime.Database,
                Username = runtime.Login.Name,
                Password = SecureSecretFile.ReadPassword(runtime.PasswordFile),
                SslMode = runtime.SslMode,
                Pooling = false,
                IncludeErrorDetail = false,
                LogParameters = false,
            }.ConnectionString;
            Available = true;
        }
        catch (Exception ex)
        {
            if (required)
                throw new InvalidOperationException(
                    "Required integration PostgreSQL hazırlığı başarısız oldu; testler skip edilemez.", ex);

            SkipReason = $"PostgreSQL erişilemez: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public SaydinDbContext CreateContext() =>
        new(_options ?? throw new InvalidOperationException(SkipReason));

    internal SaydinDbContext CreateContext(IInterceptor interceptor)
    {
        if (_dataSource is null)
            throw new InvalidOperationException(SkipReason);

        var options = new DbContextOptionsBuilder<SaydinDbContext>()
            .UseNpgsql(_dataSource, npgsql => npgsql.MapEnum<AssetCategory>("asset_category"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        return new SaydinDbContext(options);
    }

    public SaydinDbContext CreateAdminContext() =>
        new(_adminOptions ?? throw new InvalidOperationException(SkipReason));

    internal static bool VerifyPriceAuthorityFingerprint(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
    {
        return VerifyPriceAuthorityFingerprintPart(
                   connection, transaction, PriceAuthorityFingerprintSql)
               && VerifyPriceAuthorityFingerprintPart(
                   connection, transaction, PriceAuthorityStructureFingerprintSql)
               && VerifyPriceAuthorityFingerprintPart(
                   connection, transaction, PriceAuthorityAclFingerprintSql)
               && VerifyPriceAuthorityFingerprintPart(
                   connection, transaction, PriceAuthorityChunkFingerprintSql);
    }

    private static bool VerifyPriceAuthorityFingerprintPart(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 30;
        return command.ExecuteScalar() is true;
    }

    public void Dispose() => _dataSource?.Dispose();
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}

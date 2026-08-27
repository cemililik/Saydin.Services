namespace Saydin.DataQualityAudit;

internal static class ApiTrustAuditSql
{
    // Every comparison is a set equality against PostgreSQL's canonical
    // catalog rendering. The query intentionally returns only a boolean: no
    // credential verifier, principal identifier, or other row value can enter
    // the evidence bundle.
    internal const string Structure = """
        WITH role_contract AS (
          SELECT owner_role,api_capability_role,ingestion_capability_role,
                 audit_capability_role,timescale_scheduler_role
            FROM public.saydin_role_contract
           WHERE singleton=1 AND contract_schema_version=1
             AND database_name=current_database()),
        expected_columns(relation_name,column_name,data_type,not_null,default_expression) AS (VALUES
          ('users','id','uuid',true,'gen_random_uuid()'),
          ('users','device_id','character varying(200)',false,NULL),
          ('users','email','character varying(200)',false,NULL),
          ('users','tier','character varying(20)',true,'''free''::character varying'),
          ('users','created_at','timestamp with time zone',true,'now()'),
          ('users','last_seen_at','timestamp with time zone',false,NULL),
          ('users','principal_status','character varying(32)',true,'''legacy_quarantined''::character varying'),
          ('users','principal_contract_version','integer',true,'1'),
          ('users','principal_quarantined_at','timestamp with time zone',false,'statement_timestamp()'),
          ('users','principal_revoked_at','timestamp with time zone',false,NULL),
          ('users','principal_expires_at','timestamp with time zone',false,NULL),
          ('installation_credentials','id','uuid',true,NULL),
          ('installation_credentials','principal_id','uuid',true,NULL),
          ('installation_credentials','generation','integer',true,NULL),
          ('installation_credentials','secret_hash','bytea',true,NULL),
          ('installation_credentials','hash_key_version','smallint',true,NULL),
          ('installation_credentials','state','character varying(16)',true,NULL),
          ('installation_credentials','issued_at','timestamp with time zone',true,'clock_timestamp()'),
          ('installation_credentials','pending_expires_at','timestamp with time zone',false,NULL),
          ('installation_credentials','expires_at','timestamp with time zone',false,NULL),
          ('installation_credentials','activated_at','timestamp with time zone',false,NULL),
          ('installation_credentials','revoked_at','timestamp with time zone',false,NULL),
          ('installation_credentials','rotation_parent_id','uuid',false,NULL),
          ('installation_credentials','rotation_id','uuid',false,NULL),
          ('asset_catalog_state','singleton','smallint',true,NULL),
          ('asset_catalog_state','revision','bigint',true,NULL),
          ('asset_catalog_state','catalog_sha256','bytea',true,NULL),
          ('asset_catalog_state','updated_at','timestamp with time zone',true,'clock_timestamp()')),
        actual_columns AS (
          SELECT relation.relname,attribute.attname,
                 pg_catalog.format_type(attribute.atttypid,attribute.atttypmod),
                 attribute.attnotnull,
                 pg_catalog.pg_get_expr(default_value.adbin,default_value.adrelid)
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_attribute attribute
              ON attribute.attrelid=relation.oid
             AND attribute.attnum>0 AND NOT attribute.attisdropped
            LEFT JOIN pg_catalog.pg_attrdef default_value
              ON default_value.adrelid=attribute.attrelid
             AND default_value.adnum=attribute.attnum
           WHERE relation.relnamespace='public'::pg_catalog.regnamespace
             AND relation.relname IN ('users','installation_credentials','asset_catalog_state')),
        column_drift AS (
          (SELECT * FROM expected_columns EXCEPT ALL SELECT * FROM actual_columns)
          UNION ALL
          (SELECT * FROM actual_columns EXCEPT ALL SELECT * FROM expected_columns)),
        expected_constraints(
          relation_name,name,kind,validated,delete_action,definition_sha256) AS (VALUES
          ('users','chk_users_principal_status','c',true,NULL::text,'722850d068f075cababa9064efe6c46d1bb895f5a2c12d04f478a48b7a8ccbe4'),
          ('users','chk_users_principal_contract_version','c',true,NULL,'a53faab99d6ecc8cd9aee10e842cdb473d977ddee97b83bec8a7d8e495ed7c27'),
          ('users','chk_users_principal_lifecycle','c',true,NULL,'147ed35bdf057c15f501b5e7c323d78c767a8a94f13476c543499d1edc56f514'),
          ('users','chk_users_principal_expiry','c',true,NULL,'7ae79366dd327aaa6ffcf4a8102816dce05ee559ff34f30f9b5f0c91bbb23856'),
          ('installation_credentials','installation_credentials_pkey','p',true,NULL,'8c8464f42472e42ee190fc91ca8db79b5351d3a4609040516578d229c56f6fa5'),
          ('installation_credentials','fk_installation_credentials_principal','f',true,'c','3bd23dcfde6490476c1d0c440bd2fb58c7a5bcf490c73ef34f73c8673e21bfde'),
          ('installation_credentials','fk_installation_credentials_rotation_parent','f',true,'r','b39c6f4a1291e89e845c63e24c850e0eef9ef4fed85985014ed60ca148dbe03d'),
          ('installation_credentials','chk_installation_credentials_generation','c',true,NULL,'200a0506f021418ff18bd03d809684a96a3a050cbefc6fa9a61603904e6128b9'),
          ('installation_credentials','chk_installation_credentials_hash_key_version','c',true,NULL,'4250833d9ad60c81957a79c9c83ba789ede4ede81c6cd69f2558b1bcd70a011c'),
          ('installation_credentials','chk_installation_credentials_secret_hash','c',true,NULL,'164608b6a90795cca646f151db02926202693d32821432b27c0c616089f8754f'),
          ('installation_credentials','chk_installation_credentials_state','c',true,NULL,'fbc062f750093de5e25bd35c64b71990d0f0af9f40b24d5b33c0dcd0e2a4c6d7'),
          ('installation_credentials','chk_installation_credentials_lifecycle','c',true,NULL,'9e6312b1c37ef934c7b77c3f9562852f9c6e8f2b9ca93e18d773ca3a59da32df'),
          ('installation_credentials','chk_installation_credentials_expiry','c',true,NULL,'f5275584c5cf83197357de6b63e3a041df1cc515f01eebdf4096963e51ea9a53'),
          ('installation_credentials','chk_installation_credentials_rotation','c',true,NULL,'20e6911b66b51c1e52924076f4303dd0ad18a1e19c7c5652d6b4865fdf0bf962'),
          ('installation_credentials','uq_installation_credentials_verifier','u',true,NULL,'a8109116b53f8d153c71a5e8ecaceb1307d9072a142ad27af57551b3428cdce2'),
          ('installation_credentials','uq_installation_credentials_generation','u',true,NULL,'9bfc1104ec587f1759bc6ffe651764842a0ffba4380c9d7db7773fb84e4cc6f2'),
          ('asset_catalog_state','asset_catalog_state_pkey','p',true,NULL,'d004b3efcdc4a0108ecbe83c93408f63eebecc563529a3941a4c59667835f25b'),
          ('asset_catalog_state','chk_asset_catalog_state_singleton','c',true,NULL,'ca7ba2ea8fc4d647ecdaff1ffbfd4cd94e0510195f98fd436678a75273aebb3d'),
          ('asset_catalog_state','chk_asset_catalog_state_revision','c',true,NULL,'6e1b5de774e1e089aaa7ec71bc7230076978638860c68fb8a5ca3d3745130265'),
          ('asset_catalog_state','chk_asset_catalog_state_sha256','c',true,NULL,'2861ff73012cf53a26f7c05d5aae143751e66286cdf7ce41a08da9ec8d250f97')),
        actual_constraints AS (
          SELECT relation.relname,constraint_record.conname,constraint_record.contype::text,
                 constraint_record.convalidated,
                 CASE WHEN constraint_record.contype='f'
                      THEN constraint_record.confdeltype::text END,
                 pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                   pg_catalog.pg_get_constraintdef(constraint_record.oid,true),'UTF8')),'hex')
            FROM pg_catalog.pg_constraint constraint_record
            JOIN pg_catalog.pg_class relation ON relation.oid=constraint_record.conrelid
           WHERE relation.relnamespace='public'::pg_catalog.regnamespace
             AND (relation.relname IN ('installation_credentials','asset_catalog_state')
                  OR (relation.relname='users'
                      AND constraint_record.conname LIKE 'chk\_users\_principal\_%' ESCAPE '\'))),
        constraint_drift AS (
          (SELECT * FROM expected_constraints EXCEPT ALL SELECT * FROM actual_constraints)
          UNION ALL
          (SELECT * FROM actual_constraints EXCEPT ALL SELECT * FROM expected_constraints)),
        expected_indexes(
          relation_name,index_name,is_primary,is_unique,is_valid,is_ready,
          nulls_not_distinct,definition_sha256) AS (VALUES
          ('installation_credentials','installation_credentials_pkey',true,true,true,true,false,'96269248b7e49b634cfbe44d9ce85f75b55499fa45453883ef3c35d6325d8052'),
          ('installation_credentials','uq_installation_credentials_active_principal',false,true,true,true,false,'3d07ecab089c05603354479edf72292adb3ca66f45d9c425328deb5992f886bf'),
          ('installation_credentials','uq_installation_credentials_generation',false,true,true,true,false,'4f587bce549f6308e64062cd26af597b9026cb615fe66a9b634f6b0897ebbc7e'),
          ('installation_credentials','uq_installation_credentials_pending_principal',false,true,true,true,false,'c1f3939319449b77cfaccef5251918e3212fbd95ef65df82ffdb23c370b4b655'),
          ('installation_credentials','uq_installation_credentials_rotation_id',false,true,true,true,false,'2e1e33f5635e2e16e91893c9e78cdc0af712899ddd29e3dd5b042e7acde96936'),
          ('installation_credentials','uq_installation_credentials_verifier',false,true,true,true,false,'d4f4a0c99a1c3396855613d9c66b7d7872e8d8def2364a8ef954a7cf90c33a6c'),
          ('asset_catalog_state','asset_catalog_state_pkey',true,true,true,true,false,'2cb7520c67de4eb00b6fa4ea24a7130030a117ec63dd7117080f7b55a68c1258')),
        actual_indexes AS (
          SELECT relation.relname,index_relation.relname,index_record.indisprimary,
                 index_record.indisunique,index_record.indisvalid,index_record.indisready,
                 index_record.indnullsnotdistinct,
                 pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                   pg_catalog.pg_get_indexdef(index_record.indexrelid),'UTF8')),'hex')
            FROM pg_catalog.pg_index index_record
            JOIN pg_catalog.pg_class relation ON relation.oid=index_record.indrelid
            JOIN pg_catalog.pg_class index_relation ON index_relation.oid=index_record.indexrelid
           WHERE relation.relnamespace='public'::pg_catalog.regnamespace
             AND relation.relname IN ('installation_credentials','asset_catalog_state')),
        index_drift AS (
          (SELECT * FROM expected_indexes EXCEPT ALL SELECT * FROM actual_indexes)
          UNION ALL
          (SELECT * FROM actual_indexes EXCEPT ALL SELECT * FROM expected_indexes)),
        relation_security_drift AS (
          SELECT 1
            FROM (VALUES ('users'),('assets'),('installation_credentials'),('asset_catalog_state'))
                 expected(relation_name)
            LEFT JOIN pg_catalog.pg_class relation
              ON relation.relnamespace='public'::pg_catalog.regnamespace
             AND relation.relname=expected.relation_name
            CROSS JOIN role_contract
           WHERE relation.oid IS NULL OR relation.relkind<>'r'
              OR pg_catalog.pg_get_userbyid(relation.relowner)<>role_contract.owner_role
              OR relation.relrowsecurity OR relation.relforcerowsecurity
              OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy
                          WHERE policy.polrelid=relation.oid)),
        expected_table_acl(relation_name,grantee,grantor,privilege_type,is_grantable) AS (
          SELECT 'users',api_capability_role,owner_role,'SELECT',false FROM role_contract
          UNION ALL
          SELECT 'assets',api_capability_role,owner_role,'SELECT',false FROM role_contract
          UNION ALL
          SELECT 'assets',ingestion_capability_role,owner_role,'SELECT',false FROM role_contract
          UNION ALL
          SELECT 'assets',audit_capability_role,owner_role,'SELECT',false FROM role_contract
          UNION ALL
          SELECT 'asset_catalog_state',audit_capability_role,owner_role,'SELECT',false
            FROM role_contract),
        actual_table_acl AS (
          SELECT relation.relname,grantee.rolname,grantor.rolname,
                 acl.privilege_type,acl.is_grantable
            FROM pg_catalog.pg_class relation
            CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(
              relation.relacl,pg_catalog.acldefault('r',relation.relowner))) acl
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
            LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
           WHERE relation.relnamespace='public'::pg_catalog.regnamespace
             AND relation.relname IN ('users','assets','installation_credentials','asset_catalog_state')
             AND acl.grantee<>relation.relowner),
        table_acl_drift AS (
          (SELECT * FROM expected_table_acl EXCEPT ALL SELECT * FROM actual_table_acl)
          UNION ALL
          (SELECT * FROM actual_table_acl EXCEPT ALL SELECT * FROM expected_table_acl)),
        expected_column_acl(
          relation_name,column_name,grantee,grantor,privilege_type,is_grantable) AS (
          SELECT 'users',column_name,api_capability_role,owner_role,'INSERT',false
            FROM (VALUES ('id'),('device_id'),('tier'),('created_at'),('last_seen_at'))
                 columns(column_name)
            CROSS JOIN role_contract
          UNION ALL
          SELECT 'users','last_seen_at',api_capability_role,owner_role,'UPDATE',false
            FROM role_contract),
        actual_column_acl AS (
          SELECT relation.relname,attribute.attname,grantee.rolname,grantor.rolname,
                 acl.privilege_type,acl.is_grantable
            FROM pg_catalog.pg_attribute attribute
            JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
            CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
            LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
           WHERE relation.relnamespace='public'::pg_catalog.regnamespace
             AND relation.relname IN ('users','assets','installation_credentials','asset_catalog_state')
             AND attribute.attnum>0 AND NOT attribute.attisdropped
             AND acl.grantee<>relation.relowner),
        column_acl_drift AS (
          (SELECT * FROM expected_column_acl EXCEPT ALL SELECT * FROM actual_column_acl)
          UNION ALL
          (SELECT * FROM actual_column_acl EXCEPT ALL SELECT * FROM expected_column_acl)),
        expected_functions(
          name,identity_arguments,result_type,language,volatility,strict,
          security_definer,parallel,kind,leakproof,configuration,body_sha256) AS (VALUES
          ('compute_asset_catalog_sha256','','bytea','sql','s',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'23d8e0f7e620d3881a279b46b1b61347b4ff54cd20f259c575c26b56f7787efb'),
          ('refresh_asset_catalog_state','','trigger','plpgsql','v',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'ab2a18e6003ef4cdffa109309bf9e43e0b05bd2184fd2e6198e92bf399c5fb1b'),
          ('register_installation','p_principal_id uuid, p_credential_id uuid, p_secret_hash bytea, p_key_version smallint','TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)','plpgsql','v',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'51c6b4e541e0748d89a3073494759e86b8db129b6d1da566304c1947941de1b4'),
          ('resolve_installation','p_secret_hash bytea, p_key_version smallint','TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)','sql','s',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'faf69e73b08925e06b6af5f3d70285de7be49caa032652eaa7de1bfea4ff1a0d'),
          ('resolve_installation_and_rehash','p_secret_hash bytea, p_key_version smallint, p_active_secret_hash bytea, p_active_key_version smallint','TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)','plpgsql','v',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'b009448de892a425e191e649fbd942b6dd77777fa68d9b339b8010cadcbb3de2'),
          ('begin_installation_rotation','p_current_secret_hash bytea, p_current_key_version smallint, p_rotation_id uuid, p_new_credential_id uuid, p_new_secret_hash bytea, p_new_key_version smallint','TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)','plpgsql','v',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'87dca3d9934377fb2dbf6135ea4d0909b50c9821a4061c39820661ceadb20ebc'),
          ('commit_installation_rotation','p_rotation_id uuid, p_new_secret_hash bytea, p_new_key_version smallint','TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)','plpgsql','v',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'225d9fab5444eef40a37d368263ad84508a52dfa28727178e20960ec95a8b371'),
          ('revoke_installation','p_secret_hash bytea, p_key_version smallint','TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)','plpgsql','v',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'23632c1b5140a65b8c9aec4850bdc5612da2ff59eca3456a49e004dbf81c7380'),
          ('get_asset_catalog_state','','TABLE(revision bigint, catalog_sha256 bytea, updated_at timestamp with time zone)','sql','s',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'1332bd64e33389696ebed64ec1ca9fd96464fdc28693a3efbae0c6068f949c29'),
          ('installation_verifier_matches','p_expected bytea, p_candidate bytea','boolean','plpgsql','i',true,false,'s','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'0fd89e2c59f51af516bc0a028699f24e454dba6c9b37c2e1dd0ab23e82fa1c09'),
          ('resolve_installation_rotation_commit','p_rotation_id uuid, p_secret_hash bytea, p_key_version smallint','TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)','sql','s',false,true,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'00da525d7b48949f14d10ffee3b21989d9cbf6f47d201b59043c83b13c8386b1'),
          ('enforce_activity_action_allowlist','','trigger','plpgsql','v',false,false,'u','f',false,ARRAY['search_path=pg_catalog, pg_temp']::text[],'e3bef3c7edc15170f84e99e69683b0ac32e87e023e3416eac2cbafbbd70d3fcc')),
        expected_function_contract AS (
          SELECT expected.*,
                 CASE WHEN expected.name='enforce_activity_action_allowlist'
                      THEN role_contract.timescale_scheduler_role
                      ELSE role_contract.owner_role END
            FROM expected_functions expected CROSS JOIN role_contract),
        actual_function_contract AS (
          SELECT function.proname,
                 pg_catalog.pg_get_function_identity_arguments(function.oid),
                 pg_catalog.pg_get_function_result(function.oid),language.lanname,
                 function.provolatile::text,function.proisstrict,function.prosecdef,
                 function.proparallel::text,function.prokind::text,function.proleakproof,
                 function.proconfig,
                 pg_catalog.encode(pg_catalog.sha256(
                   pg_catalog.convert_to(function.prosrc,'UTF8')),'hex'),
                 pg_catalog.pg_get_userbyid(function.proowner)
            FROM pg_catalog.pg_proc function
            JOIN pg_catalog.pg_language language ON language.oid=function.prolang
           WHERE function.pronamespace='public'::pg_catalog.regnamespace
             AND function.proname IN (SELECT name FROM expected_functions)),
        function_drift AS (
          (SELECT * FROM expected_function_contract EXCEPT ALL
           SELECT * FROM actual_function_contract)
          UNION ALL
          (SELECT * FROM actual_function_contract EXCEPT ALL
           SELECT * FROM expected_function_contract)),
        expected_function_acl(
          name,identity_arguments,grantee,grantor,privilege_type,is_grantable) AS (
          SELECT expected.name,expected.identity_arguments,role_contract.api_capability_role,
                 role_contract.owner_role,'EXECUTE',false
            FROM expected_functions expected CROSS JOIN role_contract
           WHERE expected.name IN ('register_installation','resolve_installation',
             'resolve_installation_and_rehash',
             'begin_installation_rotation','commit_installation_rotation',
             'revoke_installation','get_asset_catalog_state',
             'resolve_installation_rotation_commit')),
        actual_function_acl AS (
          SELECT function.proname,
                 pg_catalog.pg_get_function_identity_arguments(function.oid),
                 grantee.rolname,grantor.rolname,acl.privilege_type,acl.is_grantable
            FROM pg_catalog.pg_proc function
            CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(
              function.proacl,pg_catalog.acldefault('f',function.proowner))) acl
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
            LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
           WHERE function.pronamespace='public'::pg_catalog.regnamespace
             AND function.proname IN (SELECT name FROM expected_functions)
             AND acl.grantee<>function.proowner),
        function_acl_drift AS (
          (SELECT * FROM expected_function_acl EXCEPT ALL SELECT * FROM actual_function_acl)
          UNION ALL
          (SELECT * FROM actual_function_acl EXCEPT ALL SELECT * FROM expected_function_acl)),
        expected_triggers(
          relation_name,trigger_name,function_schema,function_name,trigger_type,
          attribute_numbers,enabled,internal) AS (VALUES
          ('assets','trg_asset_catalog_revision_insert','public','refresh_asset_catalog_state',4,'','O',false),
          ('assets','trg_asset_catalog_revision_update','public','refresh_asset_catalog_state',16,'','O',false),
          ('assets','trg_asset_catalog_revision_delete','public','refresh_asset_catalog_state',8,'','O',false),
          ('assets','trg_asset_catalog_revision_truncate','public','refresh_asset_catalog_state',32,'','O',false),
          ('activity_logs','trg_activity_action_allowlist','public','enforce_activity_action_allowlist',23,'4','O',false)),
        actual_triggers AS (
          SELECT relation.relname,trigger.tgname,function_namespace.nspname,function.proname,
                 trigger.tgtype::integer,trigger.tgattr::text,
                 trigger.tgenabled::text,trigger.tgisinternal
            FROM pg_catalog.pg_trigger trigger
            JOIN pg_catalog.pg_class relation ON relation.oid=trigger.tgrelid
            JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
            JOIN pg_catalog.pg_namespace function_namespace
              ON function_namespace.oid=function.pronamespace
           WHERE relation.relnamespace='public'::pg_catalog.regnamespace
             AND relation.relname IN ('assets','activity_logs')
             AND (trigger.tgname LIKE 'trg\_asset\_catalog\_revision\_%' ESCAPE '\'
                  OR function.proname='refresh_asset_catalog_state'
                  OR trigger.tgname='trg_activity_action_allowlist'
                  OR function.proname='enforce_activity_action_allowlist')),
        trigger_drift AS (
          (SELECT * FROM expected_triggers EXCEPT ALL SELECT * FROM actual_triggers)
          UNION ALL
          (SELECT * FROM actual_triggers EXCEPT ALL SELECT * FROM expected_triggers))
        SELECT (SELECT count(*) FROM role_contract)=1
           AND NOT EXISTS(SELECT 1 FROM column_drift)
           AND NOT EXISTS(SELECT 1 FROM constraint_drift)
           AND NOT EXISTS(SELECT 1 FROM index_drift)
           AND NOT EXISTS(SELECT 1 FROM relation_security_drift)
           AND NOT EXISTS(SELECT 1 FROM table_acl_drift)
           AND NOT EXISTS(SELECT 1 FROM column_acl_drift)
           AND NOT EXISTS(SELECT 1 FROM function_drift)
           AND NOT EXISTS(SELECT 1 FROM function_acl_drift)
           AND NOT EXISTS(SELECT 1 FROM trigger_drift)
        """;

    // Recompute the catalog digest independently of the SECURITY DEFINER
    // publisher so a mutually-corrupted function and singleton cannot agree on
    // a forged cache namespace. NULL, short, duplicate, or absent singleton
    // evidence evaluates to false rather than aborting the audit.
    internal const string AssetCatalogState = """
        WITH canonical AS (
          SELECT pg_catalog.sha256(pg_catalog.convert_to(
            coalesce(
              (SELECT pg_catalog.jsonb_agg(
                        pg_catalog.jsonb_build_object(
                          'id',asset.id::text,
                          'symbol',asset.symbol,
                          'display_name',asset.display_name,
                          'category',asset.category::text,
                          'is_active',asset.is_active,
                          'source',asset.source,
                          'source_id',asset.source_id,
                          'metadata',asset.metadata)
                        ORDER BY asset.id)::text
                 FROM public.assets asset),
              '[]'),
            'UTF8')) AS catalog_sha256),
        state_check AS (
          SELECT count(*) AS row_count,
                 pg_catalog.bool_and(
                   state.singleton=1
                   AND state.revision>0
                   AND pg_catalog.octet_length(state.catalog_sha256)=32
                   AND state.catalog_sha256=canonical.catalog_sha256) AS valid
            FROM public.asset_catalog_state state CROSS JOIN canonical)
        SELECT row_count=1 AND coalesce(valid,false) FROM state_check
        """;
}

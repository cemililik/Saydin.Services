-- RP-07: database-local least-privilege cutover.
--
-- Cluster roles and credentials are exclusively owned by the one-shot
-- Saydin.DatabaseRoleBootstrap control-plane. This migration consumes its
-- transaction-local, already authenticated contract and never creates or
-- mutates a cluster-global role or password.

DO $contract_preflight$
DECLARE
    contract_sha text := pg_catalog.current_setting('saydin.role_contract_sha256', true);
    deployment text := pg_catalog.current_setting('saydin.deployment_id', true);
    system_hash text := pg_catalog.current_setting('saydin.system_identifier_sha256', true);
    role_prefix text := pg_catalog.current_setting('saydin.role_prefix', true);
    owner_role text := pg_catalog.current_setting('saydin.owner_role', true);
    migrator_cap text := pg_catalog.current_setting('saydin.migrator_cap_role', true);
    api_cap text := pg_catalog.current_setting('saydin.api_cap_role', true);
    ingestion_cap text := pg_catalog.current_setting('saydin.ingestion_cap_role', true);
    importer_cap text := pg_catalog.current_setting('saydin.calendar_importer_cap_role', true);
    exporter_cap text := pg_catalog.current_setting('saydin.exporter_cap_role', true);
    audit_cap text := pg_catalog.current_setting('saydin.audit_cap_role', true);
    scheduler_role text := pg_catalog.current_setting('saydin.timescale_scheduler_role', true);
    migrator_login text := pg_catalog.current_setting('saydin.migrator_login_role', true);
    login_version text := pg_catalog.current_setting('saydin.migrator_login_version', true);
    timescale_version text := pg_catalog.current_setting('saydin.timescaledb_version', true);
    uuid_version text := pg_catalog.current_setting('saydin.uuid_ossp_version', true);
    legacy_cutover text := pg_catalog.current_setting('saydin.legacy_privilege_cutover', true);
    legacy_owner text := pg_catalog.current_setting('saydin.legacy_owner_role', true);
    marker_prefix text;
    contract_material text;
    actual_system_hash text;
    actual_contract_sha text;
    expected_object_owner text;
    relation_name text;
    function_name text;
    observed_count integer;
BEGIN
    IF contract_sha !~ '^[0-9a-f]{64}$'
       OR deployment !~ '^[a-z][a-z0-9-]{2,11}$'
       OR system_hash !~ '^[0-9a-f]{64}$'
       OR role_prefix !~ '^saydin_[a-z][a-z0-9_]{2}_[0-9a-f]{24}$'
       OR login_version !~ '^[1-9][0-9]{0,2}$'
       OR timescale_version !~ '^[0-9]+(\.[0-9]+){1,3}$'
       OR uuid_version !~ '^[0-9]+(\.[0-9]+){1,3}$'
       OR legacy_cutover NOT IN ('on','off') THEN
        RAISE EXCEPTION 'role contract GUC shape rejected' USING ERRCODE='22023';
    END IF;

    IF owner_role IS DISTINCT FROM role_prefix || '_owner'
       OR migrator_cap IS DISTINCT FROM role_prefix || '_migrator_cap'
       OR api_cap IS DISTINCT FROM role_prefix || '_api_cap'
       OR ingestion_cap IS DISTINCT FROM role_prefix || '_ingestion_cap'
       OR importer_cap IS DISTINCT FROM role_prefix || '_calendar_importer_cap'
       OR exporter_cap IS DISTINCT FROM role_prefix || '_exporter_cap'
       OR audit_cap IS DISTINCT FROM role_prefix || '_audit_cap'
       OR scheduler_role IS DISTINCT FROM role_prefix || '_timescale_scheduler'
       OR migrator_login IS DISTINCT FROM role_prefix || '_migrator_login_v' || login_version THEN
        RAISE EXCEPTION 'role contract name graph rejected' USING ERRCODE='22023';
    END IF;

    SELECT pg_catalog.encode(pg_catalog.sha256(
               pg_catalog.convert_to(system_identifier::text, 'UTF8')), 'hex')
      INTO actual_system_hash
      FROM pg_catalog.pg_control_system();
    IF actual_system_hash IS DISTINCT FROM system_hash THEN
        RAISE EXCEPTION 'physical cluster identity rejected' USING ERRCODE='22023';
    END IF;

    IF (SELECT extversion FROM pg_catalog.pg_extension WHERE extname='timescaledb')
           IS DISTINCT FROM timescale_version
       OR (SELECT extversion FROM pg_catalog.pg_extension WHERE extname='uuid-ossp')
           IS DISTINCT FROM uuid_version
       OR EXISTS (
           SELECT 1 FROM pg_catalog.pg_extension
            WHERE extname IN ('timescaledb','uuid-ossp') AND extowner <> 10) THEN
        RAISE EXCEPTION 'extension contract rejected' USING ERRCODE='22023';
    END IF;

    marker_prefix := 'saydin-role-bootstrap/v1;deployment=' || deployment ||
        ';database=' || pg_catalog.current_database() || ';system=' || system_hash ||
        ';prefix=' || role_prefix || ';';

    IF EXISTS (
        SELECT 1
          FROM (VALUES
              (owner_role, 'owner', 'owner'),
              (migrator_cap, 'migrator_cap', 'capability'),
              (api_cap, 'api_cap', 'capability'),
              (ingestion_cap, 'ingestion_cap', 'capability'),
              (importer_cap, 'calendar_importer_cap', 'capability'),
              (exporter_cap, 'exporter_cap', 'capability'),
              (audit_cap, 'audit_cap', 'capability')) expected(name,purpose,kind)
          LEFT JOIN pg_catalog.pg_roles role ON role.rolname=expected.name
         WHERE role.oid IS NULL OR role.rolcanlogin OR role.rolsuper OR role.rolcreatedb
            OR role.rolcreaterole OR role.rolinherit OR role.rolreplication OR role.rolbypassrls
            OR role.rolconnlimit<>-1 OR role.rolvaliduntil IS NOT NULL OR role.rolconfig IS NOT NULL
            OR pg_catalog.shobj_description(role.oid,'pg_authid') IS DISTINCT FROM
               marker_prefix || 'purpose=' || expected.purpose || ';kind=' || expected.kind) THEN
        RAISE EXCEPTION 'stable role graph rejected' USING ERRCODE='22023';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles role
         WHERE role.rolname=scheduler_role AND role.rolcanlogin AND NOT role.rolsuper
           AND NOT role.rolcreatedb AND NOT role.rolcreaterole AND NOT role.rolinherit
           AND NOT role.rolreplication AND NOT role.rolbypassrls AND role.rolconnlimit=0
           AND role.rolvaliduntil IS NULL AND role.rolconfig IS NULL
           AND pg_catalog.shobj_description(role.oid,'pg_authid') = marker_prefix ||
               'purpose=timescale_scheduler;kind=login') THEN
        RAISE EXCEPTION 'Timescale scheduler role graph rejected' USING ERRCODE='22023';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles role
         WHERE role.rolname=migrator_login AND role.rolcanlogin AND NOT role.rolsuper
           AND NOT role.rolcreatedb AND NOT role.rolcreaterole AND NOT role.rolinherit
           AND NOT role.rolreplication AND NOT role.rolbypassrls AND role.rolconnlimit=-1
           AND role.rolvaliduntil IS NULL AND role.rolconfig IS NULL
           AND pg_catalog.shobj_description(role.oid,'pg_authid') = marker_prefix ||
               'purpose=migrator;kind=login;version=' || login_version) THEN
        RAISE EXCEPTION 'migrator login graph rejected' USING ERRCODE='22023';
    END IF;

    SELECT pg_catalog.count(*) INTO observed_count
      FROM pg_catalog.pg_auth_members membership
      JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
      JOIN pg_catalog.pg_roles member ON member.oid=membership.member
     WHERE member.rolname=migrator_login OR granted.rolname=migrator_login;
    IF observed_count<>2 OR NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_auth_members membership
        JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
        JOIN pg_catalog.pg_roles member ON member.oid=membership.member
        WHERE granted.rolname=migrator_cap AND member.rolname=migrator_login
          AND membership.grantor=10 AND NOT membership.admin_option
          AND membership.inherit_option AND NOT membership.set_option)
       OR NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_auth_members membership
        JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
        JOIN pg_catalog.pg_roles member ON member.oid=membership.member
        WHERE granted.rolname=owner_role AND member.rolname=migrator_login
          AND membership.grantor=10 AND NOT membership.admin_option
          AND NOT membership.inherit_option AND membership.set_option) THEN
        RAISE EXCEPTION 'migrator membership graph rejected' USING ERRCODE='22023';
    END IF;

    IF EXISTS (
        WITH purposes(purpose) AS (VALUES
            ('migrator'),('api'),('ingestion'),('calendar_importer'),('exporter'),('audit'))
        SELECT 1
          FROM purposes
          LEFT JOIN pg_catalog.pg_roles role
            ON role.rolname=role_prefix || '_' || purposes.purpose || '_login_v1'
         WHERE role.oid IS NULL OR NOT role.rolcanlogin OR role.rolsuper OR role.rolcreatedb
            OR role.rolcreaterole OR role.rolinherit OR role.rolreplication OR role.rolbypassrls
            OR role.rolconnlimit<>-1 OR role.rolvaliduntil IS NOT NULL OR role.rolconfig IS NOT NULL
            OR pg_catalog.shobj_description(role.oid,'pg_authid') IS DISTINCT FROM
               marker_prefix || 'purpose=' || purposes.purpose || ';kind=login;version=1') THEN
        RAISE EXCEPTION 'required v1 login graph rejected' USING ERRCODE='22023';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM pg_catalog.pg_roles role
         WHERE pg_catalog.left(role.rolname,pg_catalog.length(role_prefix)+1)=role_prefix||'_'
           AND role.rolname NOT IN (
               owner_role,migrator_cap,api_cap,ingestion_cap,importer_cap,exporter_cap,audit_cap,
               scheduler_role)
           AND NOT EXISTS (
               SELECT 1
                 FROM (VALUES
                     ('migrator',migrator_cap),('api',api_cap),('ingestion',ingestion_cap),
                     ('calendar_importer',importer_cap),('exporter',exporter_cap),('audit',audit_cap)
                 ) purpose(name,capability)
                WHERE role.rolname ~ ('^' || role_prefix || '_' || purpose.name ||
                                      '_login_v[1-9][0-9]{0,2}$')
                  AND role.rolcanlogin AND NOT role.rolsuper AND NOT role.rolcreatedb
                  AND NOT role.rolcreaterole AND NOT role.rolinherit AND NOT role.rolreplication
                  AND NOT role.rolbypassrls AND role.rolconnlimit=-1
                  AND role.rolvaliduntil IS NULL AND role.rolconfig IS NULL
                  AND pg_catalog.shobj_description(role.oid,'pg_authid') = marker_prefix ||
                      'purpose=' || purpose.name || ';kind=login;version=' ||
                      pg_catalog.substring(role.rolname, '_login_v([1-9][0-9]{0,2})$'))) THEN
        RAISE EXCEPTION 'unknown or malformed managed role rejected' USING ERRCODE='22023';
    END IF;

    IF EXISTS (
        WITH login_roles AS (
            SELECT role.oid,role.rolname,purpose.name AS purpose,purpose.capability
              FROM pg_catalog.pg_roles role
              JOIN (VALUES
                  ('migrator',migrator_cap),('api',api_cap),('ingestion',ingestion_cap),
                  ('calendar_importer',importer_cap),('exporter',exporter_cap),('audit',audit_cap)
              ) purpose(name,capability)
                ON role.rolname ~ ('^' || role_prefix || '_' || purpose.name ||
                                   '_login_v[1-9][0-9]{0,2}$')
        ),
        expected(granted,member,grantor,admin_option,inherit_option,set_option) AS (
            SELECT capability,rolname,10::oid,false,true,false FROM login_roles
            UNION ALL
            SELECT owner_role,rolname,10::oid,false,false,true
              FROM login_roles WHERE purpose='migrator'
            UNION ALL
            SELECT 'pg_monitor',exporter_cap,10::oid,false,true,false
            UNION ALL
            SELECT scheduler_role,owner_role,10::oid,false,false,true
        ),
        actual AS (
            SELECT granted.rolname,member.rolname,membership.grantor,
                   membership.admin_option,membership.inherit_option,membership.set_option
              FROM pg_catalog.pg_auth_members membership
              JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
              JOIN pg_catalog.pg_roles member ON member.oid=membership.member
             WHERE pg_catalog.left(granted.rolname,pg_catalog.length(role_prefix)+1)=role_prefix||'_'
                OR pg_catalog.left(member.rolname,pg_catalog.length(role_prefix)+1)=role_prefix||'_'
        ),
        differences AS (
            (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
            UNION ALL
            (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)
        )
        SELECT 1 FROM differences) THEN
        RAISE EXCEPTION 'complete membership graph rejected' USING ERRCODE='22023';
    END IF;

    -- Recompute the shared v1 RoleContract material with PostgreSQL 16 core
    -- sha256. Operational v2 logins do not alter this stable contract hash.
    contract_material := pg_catalog.concat_ws(E'\n',
        'contract-schema=1',
        'saydin-role-bootstrap/v1',
        'deployment=' || deployment,
        'database=' || pg_catalog.current_database(),
        'system=' || system_hash,
        'prefix=' || role_prefix,
        'extension=timescaledb:' || timescale_version,
        'extension=uuid-ossp:' || uuid_version,
        'database-owner=' || owner_role,
        'public-database=none',
        'public-schema=none',
        'database-connect=' || pg_catalog.concat_ws(',',migrator_cap,api_cap,ingestion_cap,
            importer_cap,exporter_cap,audit_cap,scheduler_role),
        'schema-usage=' || pg_catalog.concat_ws(',',api_cap,ingestion_cap,importer_cap,audit_cap,
            scheduler_role),
        'pg-control=' || pg_catalog.concat_ws(',',owner_role,migrator_cap,audit_cap),
        'role=' || owner_role || ':Owner:owner:none:' || marker_prefix || 'purpose=owner;kind=owner',
        'role=' || migrator_cap || ':Capability:migrator_cap:none:' || marker_prefix || 'purpose=migrator_cap;kind=capability',
        'role=' || api_cap || ':Capability:api_cap:none:' || marker_prefix || 'purpose=api_cap;kind=capability',
        'role=' || ingestion_cap || ':Capability:ingestion_cap:none:' || marker_prefix || 'purpose=ingestion_cap;kind=capability',
        'role=' || importer_cap || ':Capability:calendar_importer_cap:none:' || marker_prefix || 'purpose=calendar_importer_cap;kind=capability',
        'role=' || exporter_cap || ':Capability:exporter_cap:none:' || marker_prefix || 'purpose=exporter_cap;kind=capability',
        'role=' || audit_cap || ':Capability:audit_cap:none:' || marker_prefix || 'purpose=audit_cap;kind=capability',
        'role=' || scheduler_role || ':Login:timescale_scheduler:none:' || marker_prefix || 'purpose=timescale_scheduler;kind=login',
        'role=' || role_prefix || '_migrator_login_v1:Login:migrator:1:' || marker_prefix || 'purpose=migrator;kind=login;version=1',
        'role=' || role_prefix || '_api_login_v1:Login:api:1:' || marker_prefix || 'purpose=api;kind=login;version=1',
        'role=' || role_prefix || '_ingestion_login_v1:Login:ingestion:1:' || marker_prefix || 'purpose=ingestion;kind=login;version=1',
        'role=' || role_prefix || '_calendar_importer_login_v1:Login:calendar_importer:1:' || marker_prefix || 'purpose=calendar_importer;kind=login;version=1',
        'role=' || role_prefix || '_exporter_login_v1:Login:exporter:1:' || marker_prefix || 'purpose=exporter;kind=login;version=1',
        'role=' || role_prefix || '_audit_login_v1:Login:audit:1:' || marker_prefix || 'purpose=audit;kind=login;version=1',
        'membership=' || role_prefix || '_migrator_login_v1:' || migrator_cap || ':admin=false:inherit=true:set=false',
        'membership=' || role_prefix || '_migrator_login_v1:' || owner_role || ':admin=false:inherit=false:set=true',
        'membership=' || role_prefix || '_api_login_v1:' || api_cap || ':admin=false:inherit=true:set=false',
        'membership=' || role_prefix || '_ingestion_login_v1:' || ingestion_cap || ':admin=false:inherit=true:set=false',
        'membership=' || role_prefix || '_calendar_importer_login_v1:' || importer_cap || ':admin=false:inherit=true:set=false',
        'membership=' || role_prefix || '_exporter_login_v1:' || exporter_cap || ':admin=false:inherit=true:set=false',
        'membership=' || role_prefix || '_audit_login_v1:' || audit_cap || ':admin=false:inherit=true:set=false',
        'membership=' || owner_role || ':' || scheduler_role || ':admin=false:inherit=false:set=true',
        'membership=' || exporter_cap || ':pg_monitor:admin=false:inherit=true:set=false');
    actual_contract_sha := pg_catalog.encode(
        pg_catalog.sha256(pg_catalog.convert_to(contract_material,'UTF8')), 'hex');
    IF actual_contract_sha IS DISTINCT FROM contract_sha THEN
        RAISE EXCEPTION 'role contract sha rejected' USING ERRCODE='22023';
    END IF;

    IF pg_catalog.pg_get_userbyid((SELECT datdba FROM pg_catalog.pg_database
                                   WHERE datname=pg_catalog.current_database()))
           IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid((SELECT nspowner FROM pg_catalog.pg_namespace
                                      WHERE nspname='public')) <> 'pg_database_owner' THEN
        RAISE EXCEPTION 'database/schema ownership contract rejected' USING ERRCODE='22023';
    END IF;

    IF legacy_cutover='off' THEN
        IF current_user IS DISTINCT FROM owner_role
           OR session_user IS DISTINCT FROM migrator_login THEN
            RAISE EXCEPTION 'fresh owner session contract rejected' USING ERRCODE='42501';
        END IF;
        expected_object_owner := owner_role;
    ELSE
        IF current_user IS DISTINCT FROM session_user
           OR (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=session_user)<>10
           OR NOT (SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname=session_user)
           OR legacy_owner IS DISTINCT FROM session_user THEN
            RAISE EXCEPTION 'legacy cutover session contract rejected' USING ERRCODE='42501';
        END IF;
        expected_object_owner := legacy_owner;
    END IF;

    FOREACH relation_name IN ARRAY ARRAY[
        'activity_logs','asset_market_calendars','assets','inflation_rates','ingestion_jobs',
        'ingestion_windows','market_calendar_active_releases','market_calendar_days',
        'market_calendar_release_sources','market_calendar_releases','market_calendars',
        'market_holidays','price_points','saved_scenarios','schema_migrations',
        'saydin_migration_control','users']
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE namespace.nspname='public' AND relation.relname=relation_name
              AND relation.relkind IN ('r','p')
              AND pg_catalog.pg_get_userbyid(relation.relowner)=expected_object_owner) THEN
            RAISE EXCEPTION 'application relation ownership preflight rejected: %', relation_name
                USING ERRCODE='42501';
        END IF;
    END LOOP;

    IF EXISTS (
        WITH expected(signature,body_sha256,expected_config) AS (VALUES
            ('activate_market_calendar_release(text,uuid,uuid)','9c7680d37e98ae75475ccac4afc96798f958cf0ea897c7f2ded6fdb19879c9c9','search_path=pg_catalog, public, pg_temp'),
            ('enforce_active_market_calendar_release()','5bade313804c0e597b9af28a6edb1143600eaefd5345cb347fe9daedd5f4ee6f','search_path=pg_catalog, public, pg_temp'),
            ('enforce_asset_market_calendar_source()','32086cca3e0712a9fac701f54b7801ef43a1bfb2574d1fdcb03485680cbbe2f4','search_path=pg_catalog, public, pg_temp'),
            ('enforce_inflation_rate_ingestion_fence()','ceae4a377df47e9a268e0e37f347c8ef17f56afcb819c3d8a762852530fbffaa','search_path=pg_catalog, public'),
            ('enforce_ingestion_window_calendar_release()','757f159bc79d46513b43dc58dff2127d097128ac1ba0d22e470a1cfc933fcc1a','search_path=pg_catalog, public, pg_temp'),
            ('enforce_market_calendar_release_assembly()','7a00eb9a7c0a8e266ff7f10543efa757924dec410fe573eac728a38adbe679db','search_path=pg_catalog, public, pg_temp'),
            ('enforce_price_point_ingestion_fence()','4e64afe06288d5700543dd7565505935b7ab74e5a102b00f6d9c56ed4290a416','search_path=pg_catalog, public'),
            ('enforce_saved_scenario_hard_cap()','c4bd2c3b9f61faa2394bd9d5eec0075043ba8e16680a8b6e7882d9f37854c42c','search_path=pg_catalog, public, pg_temp'),
            ('provision_asset_market_calendar()','32c07aca82ae50c6b3638015df2792bd421a4aa3e09f85aea9089dd0b3ec7392','search_path=pg_catalog, public, pg_temp'),
            ('seal_market_calendar_release(uuid)','d41d5fb586594ec56de83c6a85e71108a292e8d1adb23863dae4b9de137a3e4c','search_path=pg_catalog, public, pg_temp'),
            ('verify_market_calendar_release_payload(uuid)','1efd84a21cb751b2a4254bd5440feaf716e7bd69390f7aeb9c02ba45665d84dd','search_path=pg_catalog, public, pg_temp')
        )
        SELECT 1
          FROM expected
          LEFT JOIN pg_catalog.pg_proc function
            ON function.oid=('public.'||expected.signature)::pg_catalog.regprocedure
          LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.oid=function.pronamespace
          LEFT JOIN pg_catalog.pg_language language ON language.oid=function.prolang
         WHERE function.oid IS NULL OR namespace.nspname<>'public'
            OR pg_catalog.pg_get_userbyid(function.proowner)<>expected_object_owner
            OR function.prosecdef OR function.provolatile<>'v' OR language.lanname<>'plpgsql'
            OR function.proconfig<>ARRAY[expected.expected_config]::text[]
            OR pg_catalog.encode(pg_catalog.sha256(
                   pg_catalog.convert_to(function.prosrc,'UTF8')),'hex')<>expected.body_sha256) THEN
        RAISE EXCEPTION 'application function fingerprint preflight rejected'
            USING ERRCODE='42501';
    END IF;

    IF EXISTS (
        WITH expected(name,relation_name,function_name,trigger_type,enabled) AS (VALUES
            ('trg_asset_market_calendar_source','asset_market_calendars','enforce_asset_market_calendar_source',31,'O'),
            ('trg_asset_market_calendars_no_truncate','asset_market_calendars','enforce_asset_market_calendar_source',34,'O'),
            ('trg_assets_calendar_provision','assets','provision_asset_market_calendar',5,'O'),
            ('trg_assets_calendar_source_immutable','assets','provision_asset_market_calendar',19,'O'),
            ('trg_inflation_rates_ingestion_fence','inflation_rates','enforce_inflation_rate_ingestion_fence',23,'A'),
            ('trg_ingestion_window_calendar_release','ingestion_windows','enforce_ingestion_window_calendar_release',23,'O'),
            ('trg_market_calendar_active_release_sealed','market_calendar_active_releases','enforce_active_market_calendar_release',31,'O'),
            ('trg_market_calendar_active_releases_no_truncate','market_calendar_active_releases','enforce_active_market_calendar_release',34,'O'),
            ('trg_market_calendar_days_immutable','market_calendar_days','enforce_market_calendar_release_assembly',31,'O'),
            ('trg_market_calendar_days_no_truncate','market_calendar_days','enforce_market_calendar_release_assembly',34,'O'),
            ('trg_market_calendar_release_sources_immutable','market_calendar_release_sources','enforce_market_calendar_release_assembly',31,'O'),
            ('trg_market_calendar_release_sources_no_truncate','market_calendar_release_sources','enforce_market_calendar_release_assembly',34,'O'),
            ('trg_market_calendar_releases_immutable','market_calendar_releases','enforce_market_calendar_release_assembly',27,'O'),
            ('trg_market_calendar_releases_no_truncate','market_calendar_releases','enforce_market_calendar_release_assembly',34,'O'),
            ('trg_price_points_ingestion_fence','price_points','enforce_price_point_ingestion_fence',23,'O'),
            ('trg_saved_scenarios_hard_cap','saved_scenarios','enforce_saved_scenario_hard_cap',7,'O')
        )
        SELECT 1
          FROM expected
          LEFT JOIN pg_catalog.pg_trigger trigger
            ON trigger.tgname=expected.name
           AND trigger.tgrelid=('public.'||expected.relation_name)::pg_catalog.regclass
           AND NOT trigger.tgisinternal
          LEFT JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
          LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.oid=function.pronamespace
         WHERE trigger.oid IS NULL OR trigger.tgtype<>expected.trigger_type
            OR trigger.tgenabled<>expected.enabled OR function.proname<>expected.function_name
            OR namespace.nspname<>'public') THEN
        RAISE EXCEPTION 'application trigger fingerprint preflight rejected'
            USING ERRCODE='42501';
    END IF;

    IF pg_catalog.pg_get_userbyid((SELECT typowner FROM pg_catalog.pg_type type
        JOIN pg_catalog.pg_namespace namespace ON namespace.oid=type.typnamespace
        WHERE namespace.nspname='public' AND type.typname='asset_category'))
       IS DISTINCT FROM expected_object_owner THEN
        RAISE EXCEPTION 'asset category ownership preflight rejected' USING ERRCODE='42501';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM pg_catalog.pg_type type
          CROSS JOIN LATERAL pg_catalog.aclexplode(
              coalesce(type.typacl,pg_catalog.acldefault('T',type.typowner))) acl
         WHERE type.oid='public.asset_category'::pg_catalog.regtype
           AND acl.grantee<>type.typowner
           AND (acl.grantee<>0 OR acl.privilege_type<>'USAGE' OR acl.is_grantable)) THEN
        RAISE EXCEPTION 'asset category ACL preflight rejected' USING ERRCODE='42501';
    END IF;

    IF EXISTS (
        WITH chunk_names AS (
            SELECT chunks.chunk_schema AS schema_name,chunks.chunk_name AS table_name
              FROM timescaledb_information.chunks chunks
             WHERE chunks.hypertable_schema='public'
               AND chunks.hypertable_name IN ('price_points','activity_logs')
            UNION
            SELECT compressed.schema_name,compressed.table_name
              FROM _timescaledb_catalog.chunk source
              JOIN _timescaledb_catalog.chunk compressed
                ON compressed.id=source.compressed_chunk_id
             WHERE source.hypertable_id IN (
                 SELECT hypertable.id FROM _timescaledb_catalog.hypertable hypertable
                  WHERE hypertable.schema_name='public'
                    AND hypertable.table_name IN ('price_points','activity_logs'))
               AND NOT compressed.dropped
        )
        SELECT 1
          FROM chunk_names
          LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.nspname=chunk_names.schema_name
          LEFT JOIN pg_catalog.pg_class relation
            ON relation.relnamespace=namespace.oid AND relation.relname=chunk_names.table_name
         WHERE relation.oid IS NULL OR relation.relkind NOT IN ('r','p')
            OR pg_catalog.pg_get_userbyid(relation.relowner) IS DISTINCT FROM expected_object_owner
            OR relation.relrowsecurity OR relation.relforcerowsecurity
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy
                        WHERE policy.polrelid=relation.oid)
            OR EXISTS (
                SELECT 1 FROM pg_catalog.aclexplode(relation.relacl) acl
                LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE acl.grantee<>relation.relowner
                   AND NOT (grantee.rolname='saydin_exporter'
                            AND grantor.rolname=expected_object_owner
                            AND acl.privilege_type='SELECT' AND NOT acl.is_grantable))) THEN
        RAISE EXCEPTION 'Timescale chunk security preflight rejected' USING ERRCODE='42501';
    END IF;

    FOREACH function_name IN ARRAY ARRAY[
        'activate_market_calendar_release(text,uuid,uuid)',
        'enforce_active_market_calendar_release()',
        'enforce_asset_market_calendar_source()',
        'enforce_inflation_rate_ingestion_fence()',
        'enforce_ingestion_window_calendar_release()',
        'enforce_market_calendar_release_assembly()',
        'enforce_price_point_ingestion_fence()',
        'enforce_saved_scenario_hard_cap()',
        'provision_asset_market_calendar()',
        'seal_market_calendar_release(uuid)',
        'verify_market_calendar_release_payload(uuid)']
    LOOP
        IF pg_catalog.pg_get_userbyid((SELECT proowner FROM pg_catalog.pg_proc
            WHERE oid=('public.' || function_name)::pg_catalog.regprocedure))
           IS DISTINCT FROM expected_object_owner THEN
            RAISE EXCEPTION 'application function ownership preflight rejected: %', function_name
                USING ERRCODE='42501';
        END IF;
    END LOOP;

    -- Pre-existing object grants are either PostgreSQL defaults or the exact
    -- legacy 012b exporter SELECT grant. Any other grantee is drift and aborts
    -- the whole transaction before ownership changes.
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_class relation
        JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
        CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) acl
        LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
        WHERE namespace.nspname='public' AND relation.relname=ANY(ARRAY[
            'activity_logs','asset_market_calendars','assets','inflation_rates','ingestion_jobs',
            'ingestion_windows','market_calendar_active_releases','market_calendar_days',
            'market_calendar_release_sources','market_calendar_releases','market_calendars',
            'market_holidays','price_points','saved_scenarios','schema_migrations',
            'saydin_migration_control','users'])
          AND acl.grantee<>relation.relowner
          AND NOT (grantee.rolname='saydin_exporter' AND acl.privilege_type='SELECT'
                   AND relation.relname IN ('activity_logs','price_points','inflation_rates')
                   AND NOT acl.is_grantable)) THEN
        RAISE EXCEPTION 'pre-existing application ACL drift rejected' USING ERRCODE='42501';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM pg_catalog.pg_attribute attribute
          JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
          JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
         WHERE namespace.nspname='public'
           AND relation.relname=ANY(ARRAY[
               'activity_logs','asset_market_calendars','assets','inflation_rates','ingestion_jobs',
               'ingestion_windows','market_calendar_active_releases','market_calendar_days',
               'market_calendar_release_sources','market_calendar_releases','market_calendars',
               'market_holidays','price_points','saved_scenarios','schema_migrations',
               'saydin_migration_control','users'])
           AND attribute.attnum>0 AND NOT attribute.attisdropped AND attribute.attacl IS NOT NULL) THEN
        RAISE EXCEPTION 'pre-existing column ACL drift rejected' USING ERRCODE='42501';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM pg_catalog.pg_class relation
          JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
         WHERE namespace.nspname='public'
           AND relation.relname=ANY(ARRAY[
               'activity_logs','asset_market_calendars','assets','inflation_rates','ingestion_jobs',
               'ingestion_windows','market_calendar_active_releases','market_calendar_days',
               'market_calendar_release_sources','market_calendar_releases','market_calendars',
               'market_holidays','price_points','saved_scenarios','schema_migrations',
               'saydin_migration_control','users'])
           AND (relation.relrowsecurity OR relation.relforcerowsecurity
                OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy
                            WHERE policy.polrelid=relation.oid))) THEN
        RAISE EXCEPTION 'pre-existing row security drift rejected' USING ERRCODE='42501';
    END IF;

    IF EXISTS (
        WITH functions(signature,public_execute) AS (VALUES
            ('activate_market_calendar_release(text,uuid,uuid)',false),
            ('enforce_active_market_calendar_release()',true),
            ('enforce_asset_market_calendar_source()',true),
            ('enforce_inflation_rate_ingestion_fence()',true),
            ('enforce_ingestion_window_calendar_release()',true),
            ('enforce_market_calendar_release_assembly()',true),
            ('enforce_price_point_ingestion_fence()',true),
            ('enforce_saved_scenario_hard_cap()',false),
            ('provision_asset_market_calendar()',true),
            ('seal_market_calendar_release(uuid)',false),
            ('verify_market_calendar_release_payload(uuid)',true)
        ),
        expected(signature,grantee,grantor,privilege_type,is_grantable) AS (
            SELECT signature,expected_object_owner,expected_object_owner,'EXECUTE',false
              FROM functions
            UNION ALL
            SELECT signature,'PUBLIC',expected_object_owner,'EXECUTE',false
              FROM functions WHERE public_execute
        ),
        actual AS (
            SELECT functions.signature,coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                   acl.privilege_type,acl.is_grantable
              FROM functions
              JOIN pg_catalog.pg_proc function
                ON function.oid=('public.'||functions.signature)::pg_catalog.regprocedure
              CROSS JOIN LATERAL pg_catalog.aclexplode(
                  coalesce(function.proacl,pg_catalog.acldefault('f',function.proowner))) acl
              LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
              LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
        ),
        differences AS (
            (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
            UNION ALL
            (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)
        )
        SELECT 1 FROM differences) THEN
        RAISE EXCEPTION 'pre-existing function ACL drift rejected' USING ERRCODE='42501';
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_default_acl defaults
         WHERE defaults.defaclrole=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=owner_role)
           AND defaults.defaclnamespace IN (0,'public'::pg_catalog.regnamespace)) THEN
        RAISE EXCEPTION 'pre-existing owner default ACL drift rejected' USING ERRCODE='42501';
    END IF;
END
$contract_preflight$;

LOCK TABLE public.activity_logs, public.asset_market_calendars, public.assets,
    public.inflation_rates, public.ingestion_jobs, public.ingestion_windows,
    public.market_calendar_active_releases, public.market_calendar_days,
    public.market_calendar_release_sources, public.market_calendar_releases,
    public.market_calendars, public.market_holidays, public.price_points,
    public.saved_scenarios, public.schema_migrations,
    public.saydin_migration_control, public.users IN ACCESS EXCLUSIVE MODE;

CREATE TABLE public.saydin_role_contract (
    singleton smallint PRIMARY KEY CHECK (singleton=1),
    contract_schema_version integer NOT NULL CHECK (contract_schema_version=1),
    contract_sha256 char(64) NOT NULL CHECK (contract_sha256 ~ '^[0-9a-f]{64}$'),
    deployment_id varchar(12) NOT NULL,
    database_name varchar(63) NOT NULL,
    system_identifier_sha256 char(64) NOT NULL,
    role_prefix varchar(63) NOT NULL,
    owner_role varchar(63) NOT NULL,
    migrator_capability_role varchar(63) NOT NULL,
    api_capability_role varchar(63) NOT NULL,
    ingestion_capability_role varchar(63) NOT NULL,
    calendar_importer_capability_role varchar(63) NOT NULL,
    exporter_capability_role varchar(63) NOT NULL,
    audit_capability_role varchar(63) NOT NULL,
    timescale_scheduler_role varchar(63) NOT NULL,
    established_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp()
);

DO $ownership_and_contract$
DECLARE
    owner_role text := pg_catalog.current_setting('saydin.owner_role');
    relation_name text;
    chunk record;
BEGIN
    FOREACH relation_name IN ARRAY ARRAY[
        'asset_market_calendars','assets','inflation_rates','ingestion_jobs',
        'ingestion_windows','market_calendar_active_releases','market_calendar_days',
        'market_calendar_release_sources','market_calendar_releases','market_calendars',
        'market_holidays','price_points','saved_scenarios','schema_migrations',
        'saydin_migration_control','saydin_role_contract','users']
    LOOP
        EXECUTE pg_catalog.format('ALTER TABLE public.%I OWNER TO %I', relation_name, owner_role);
    END LOOP;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='saydin_exporter') THEN
        FOR chunk IN
            SELECT chunks.chunk_schema AS schema_name,chunks.chunk_name AS table_name
              FROM timescaledb_information.chunks chunks
             WHERE chunks.hypertable_schema='public'
               AND chunks.hypertable_name IN ('price_points','activity_logs')
            UNION
            SELECT compressed.schema_name,compressed.table_name
              FROM _timescaledb_catalog.chunk source
              JOIN _timescaledb_catalog.chunk compressed
                ON compressed.id=source.compressed_chunk_id
              JOIN _timescaledb_catalog.hypertable hypertable
                ON hypertable.id=source.hypertable_id
             WHERE hypertable.schema_name='public'
               AND hypertable.table_name IN ('price_points','activity_logs')
               AND NOT compressed.dropped
        LOOP
            EXECUTE pg_catalog.format(
                'REVOKE ALL ON TABLE %I.%I FROM saydin_exporter',
                chunk.schema_name,chunk.table_name);
        END LOOP;
    END IF;
    -- PostgreSQL requires the target owner to hold CREATE on the containing
    -- schema during ALTER OWNER. Grant it only inside this transaction and
    -- revoke it before any runtime ACL is established.
    EXECUTE pg_catalog.format('GRANT CREATE ON SCHEMA public TO %I',
        pg_catalog.current_setting('saydin.timescale_scheduler_role'));
    EXECUTE pg_catalog.format('ALTER TABLE public.activity_logs OWNER TO %I',
        pg_catalog.current_setting('saydin.timescale_scheduler_role'));
    EXECUTE pg_catalog.format('REVOKE CREATE ON SCHEMA public FROM %I',
        pg_catalog.current_setting('saydin.timescale_scheduler_role'));
    PERFORM saydin_role_control.consume_timescale_scheduler_transition();
    EXECUTE 'DROP SCHEMA saydin_role_control CASCADE';

    FOR chunk IN
        SELECT chunks.chunk_schema AS schema_name,chunks.chunk_name AS table_name,
               owner_role AS target_owner
          FROM timescaledb_information.chunks chunks
         WHERE chunks.hypertable_schema='public'
           AND chunks.hypertable_name='price_points'
        UNION
        SELECT compressed.schema_name,compressed.table_name,
               owner_role AS target_owner
          FROM _timescaledb_catalog.chunk source
          JOIN _timescaledb_catalog.chunk compressed ON compressed.id=source.compressed_chunk_id
          JOIN _timescaledb_catalog.hypertable hypertable ON hypertable.id=source.hypertable_id
         WHERE hypertable.schema_name='public'
           AND hypertable.table_name='price_points'
           AND NOT compressed.dropped
    LOOP
        EXECUTE pg_catalog.format(
            'ALTER TABLE %I.%I OWNER TO %I',chunk.schema_name,chunk.table_name,chunk.target_owner);
    END LOOP;
    EXECUTE pg_catalog.format('ALTER TYPE public.asset_category OWNER TO %I', owner_role);

    EXECUTE pg_catalog.format('ALTER FUNCTION public.activate_market_calendar_release(text,uuid,uuid) OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.enforce_active_market_calendar_release() OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.enforce_asset_market_calendar_source() OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.enforce_inflation_rate_ingestion_fence() OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.enforce_ingestion_window_calendar_release() OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.enforce_market_calendar_release_assembly() OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.enforce_price_point_ingestion_fence() OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.enforce_saved_scenario_hard_cap() OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.provision_asset_market_calendar() OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.seal_market_calendar_release(uuid) OWNER TO %I',owner_role);
    EXECUTE pg_catalog.format('ALTER FUNCTION public.verify_market_calendar_release_payload(uuid) OWNER TO %I',owner_role);

    EXECUTE pg_catalog.format('SET LOCAL ROLE %I',
        pg_catalog.current_setting('saydin.timescale_scheduler_role'));
    IF current_user IS DISTINCT FROM
       pg_catalog.current_setting('saydin.timescale_scheduler_role') THEN
        RAISE EXCEPTION 'Timescale scheduler role transition rejected' USING ERRCODE='42501';
    END IF;
    PERFORM public.remove_compression_policy('public.activity_logs'::pg_catalog.regclass,
        if_exists=>true);
    PERFORM public.add_compression_policy('public.activity_logs'::pg_catalog.regclass,
        INTERVAL '7 days',if_not_exists=>false);
    EXECUTE 'RESET ROLE';
    EXECUTE pg_catalog.format('SET ROLE %I',owner_role);

    INSERT INTO public.saydin_role_contract(
        singleton,contract_schema_version,contract_sha256,deployment_id,database_name,
        system_identifier_sha256,role_prefix,owner_role,migrator_capability_role,
        api_capability_role,ingestion_capability_role,calendar_importer_capability_role,
        exporter_capability_role,audit_capability_role,timescale_scheduler_role)
    VALUES (
        1,1,pg_catalog.current_setting('saydin.role_contract_sha256'),
        pg_catalog.current_setting('saydin.deployment_id'),pg_catalog.current_database(),
        pg_catalog.current_setting('saydin.system_identifier_sha256'),
        pg_catalog.current_setting('saydin.role_prefix'),owner_role,
        pg_catalog.current_setting('saydin.migrator_cap_role'),
        pg_catalog.current_setting('saydin.api_cap_role'),
        pg_catalog.current_setting('saydin.ingestion_cap_role'),
        pg_catalog.current_setting('saydin.calendar_importer_cap_role'),
        pg_catalog.current_setting('saydin.exporter_cap_role'),
        pg_catalog.current_setting('saydin.audit_cap_role'),
        pg_catalog.current_setting('saydin.timescale_scheduler_role'));
END
$ownership_and_contract$;

ALTER FUNCTION public.seal_market_calendar_release(uuid) SECURITY DEFINER;
ALTER FUNCTION public.seal_market_calendar_release(uuid)
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.activate_market_calendar_release(text,uuid,uuid) SECURITY DEFINER;
ALTER FUNCTION public.activate_market_calendar_release(text,uuid,uuid)
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.enforce_market_calendar_release_assembly() SECURITY DEFINER;
ALTER FUNCTION public.enforce_market_calendar_release_assembly()
    SET search_path TO pg_catalog, pg_temp;

ALTER FUNCTION public.verify_market_calendar_release_payload(uuid) SECURITY INVOKER;
ALTER FUNCTION public.verify_market_calendar_release_payload(uuid)
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.enforce_active_market_calendar_release() SECURITY INVOKER;
ALTER FUNCTION public.enforce_active_market_calendar_release()
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.enforce_asset_market_calendar_source() SECURITY INVOKER;
ALTER FUNCTION public.enforce_asset_market_calendar_source()
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.enforce_inflation_rate_ingestion_fence() SECURITY INVOKER;
ALTER FUNCTION public.enforce_inflation_rate_ingestion_fence()
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.enforce_ingestion_window_calendar_release() SECURITY INVOKER;
ALTER FUNCTION public.enforce_ingestion_window_calendar_release()
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.enforce_price_point_ingestion_fence() SECURITY INVOKER;
ALTER FUNCTION public.enforce_price_point_ingestion_fence()
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.enforce_saved_scenario_hard_cap() SECURITY INVOKER;
ALTER FUNCTION public.enforce_saved_scenario_hard_cap()
    SET search_path TO pg_catalog, pg_temp;
ALTER FUNCTION public.provision_asset_market_calendar() SECURITY INVOKER;
ALTER FUNCTION public.provision_asset_market_calendar()
    SET search_path TO pg_catalog, pg_temp;

DO $acl$
DECLARE
    database_name text := pg_catalog.current_database();
    owner_role text := pg_catalog.current_setting('saydin.owner_role');
    migrator_cap text := pg_catalog.current_setting('saydin.migrator_cap_role');
    api_cap text := pg_catalog.current_setting('saydin.api_cap_role');
    ingestion_cap text := pg_catalog.current_setting('saydin.ingestion_cap_role');
    importer_cap text := pg_catalog.current_setting('saydin.calendar_importer_cap_role');
    exporter_cap text := pg_catalog.current_setting('saydin.exporter_cap_role');
    audit_cap text := pg_catalog.current_setting('saydin.audit_cap_role');
    scheduler_role text := pg_catalog.current_setting('saydin.timescale_scheduler_role');
    managed_roles text;
    relation_name text;
    function_name text;
BEGIN
    managed_roles := pg_catalog.concat_ws(',',
        pg_catalog.quote_ident(migrator_cap),pg_catalog.quote_ident(api_cap),
        pg_catalog.quote_ident(ingestion_cap),pg_catalog.quote_ident(importer_cap),
        pg_catalog.quote_ident(exporter_cap),pg_catalog.quote_ident(audit_cap),
        pg_catalog.quote_ident(scheduler_role));

    EXECUTE pg_catalog.format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC',database_name);
    EXECUTE 'REVOKE CREATE, USAGE ON SCHEMA public FROM PUBLIC';

    FOREACH relation_name IN ARRAY ARRAY[
        'asset_market_calendars','assets','inflation_rates','ingestion_jobs',
        'ingestion_windows','market_calendar_active_releases','market_calendar_days',
        'market_calendar_release_sources','market_calendar_releases','market_calendars',
        'market_holidays','price_points','saved_scenarios','schema_migrations',
        'saydin_migration_control','saydin_role_contract','users']
    LOOP
        EXECUTE pg_catalog.format('REVOKE ALL ON TABLE public.%I FROM PUBLIC',relation_name);
        EXECUTE pg_catalog.format('REVOKE ALL ON TABLE public.%I FROM %s',relation_name,managed_roles);
    END LOOP;
    EXECUTE pg_catalog.format('SET LOCAL ROLE %I',scheduler_role);
    EXECUTE 'REVOKE ALL ON TABLE public.activity_logs FROM PUBLIC';
    EXECUTE pg_catalog.format('REVOKE ALL ON TABLE public.activity_logs FROM %s',managed_roles);
    EXECUTE pg_catalog.format('GRANT INSERT ON public.activity_logs TO %I',api_cap);
    EXECUTE 'RESET ROLE';
    EXECUTE pg_catalog.format('SET ROLE %I',owner_role);

    EXECUTE 'REVOKE ALL ON TYPE public.asset_category FROM PUBLIC';
    EXECUTE pg_catalog.format('REVOKE ALL ON TYPE public.asset_category FROM %s',managed_roles);

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='saydin_exporter') THEN
        EXECUTE 'REVOKE ALL ON TABLE public.activity_logs,public.price_points,public.inflation_rates FROM saydin_exporter';
    END IF;

    FOREACH function_name IN ARRAY ARRAY[
        'activate_market_calendar_release(text,uuid,uuid)',
        'enforce_active_market_calendar_release()',
        'enforce_asset_market_calendar_source()',
        'enforce_inflation_rate_ingestion_fence()',
        'enforce_ingestion_window_calendar_release()',
        'enforce_market_calendar_release_assembly()',
        'enforce_price_point_ingestion_fence()',
        'enforce_saved_scenario_hard_cap()',
        'provision_asset_market_calendar()',
        'seal_market_calendar_release(uuid)',
        'verify_market_calendar_release_payload(uuid)']
    LOOP
        EXECUTE pg_catalog.format('REVOKE ALL ON FUNCTION public.%s FROM PUBLIC',function_name);
        EXECUTE pg_catalog.format('REVOKE ALL ON FUNCTION public.%s FROM %s',function_name,managed_roles);
    END LOOP;

    EXECUTE pg_catalog.format(
        'GRANT SELECT ON public.assets,public.price_points,public.inflation_rates,public.users,public.saved_scenarios TO %I',api_cap);
    EXECUTE pg_catalog.format(
        'GRANT INSERT(id,device_id,tier,created_at,last_seen_at) ON public.users TO %I',api_cap);
    EXECUTE pg_catalog.format('GRANT UPDATE(last_seen_at) ON public.users TO %I',api_cap);
    EXECUTE pg_catalog.format('GRANT INSERT,DELETE ON public.saved_scenarios TO %I',api_cap);

    EXECUTE pg_catalog.format(
        'GRANT SELECT ON public.assets,public.price_points,public.inflation_rates,public.ingestion_windows,public.ingestion_jobs,public.market_calendar_releases,public.market_calendar_days,public.market_calendar_active_releases,public.asset_market_calendars,public.market_holidays TO %I',ingestion_cap);
    EXECUTE pg_catalog.format(
        'GRANT INSERT,UPDATE ON public.price_points,public.inflation_rates,public.ingestion_windows,public.ingestion_jobs TO %I',ingestion_cap);

    EXECUTE pg_catalog.format(
        'GRANT SELECT,INSERT ON public.market_calendar_releases,public.market_calendar_release_sources TO %I',importer_cap);
    EXECUTE pg_catalog.format('GRANT INSERT ON public.market_calendar_days TO %I',importer_cap);
    EXECUTE pg_catalog.format('GRANT EXECUTE ON FUNCTION public.seal_market_calendar_release(uuid) TO %I',importer_cap);
    EXECUTE pg_catalog.format('GRANT EXECUTE ON FUNCTION public.activate_market_calendar_release(text,uuid,uuid) TO %I',importer_cap);

    EXECUTE pg_catalog.format(
        'GRANT SELECT ON public.asset_market_calendars,public.assets,public.inflation_rates,public.ingestion_jobs,public.ingestion_windows,public.market_calendar_active_releases,public.market_calendar_days,public.market_calendar_release_sources,public.market_calendar_releases,public.market_calendars,public.price_points,public.schema_migrations,public.saydin_migration_control,public.saydin_role_contract TO %I',audit_cap);
    EXECUTE pg_catalog.format('GRANT EXECUTE ON FUNCTION public.verify_market_calendar_release_payload(uuid) TO %I',audit_cap);
    EXECUTE pg_catalog.format('GRANT USAGE ON TYPE public.asset_category TO %I',api_cap);
    EXECUTE pg_catalog.format('GRANT USAGE ON TYPE public.asset_category TO %I',ingestion_cap);

    EXECUTE pg_catalog.format('GRANT CONNECT ON DATABASE %I TO %I',database_name,scheduler_role);
    EXECUTE pg_catalog.format('GRANT USAGE ON SCHEMA public TO %I',scheduler_role);

    EXECUTE pg_catalog.format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC',owner_role);
    EXECUTE pg_catalog.format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC',owner_role);
    EXECUTE pg_catalog.format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC',owner_role);
    EXECUTE pg_catalog.format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE USAGE ON TYPES FROM PUBLIC',owner_role);
    IF pg_catalog.current_setting('saydin.legacy_privilege_cutover')='on' THEN
        EXECUTE 'RESET ROLE';
    END IF;
END
$acl$;

COMMENT ON TABLE public.saydin_role_contract IS
    'Database-local singleton pinning the verified cluster role contract used by migration 019.';

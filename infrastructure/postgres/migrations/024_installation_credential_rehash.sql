-- Migration 024: active installation credential verifier re-hash on successful use.
--
-- The API never sends a raw bearer credential to PostgreSQL. During key rotation it
-- supplies both the verifier that authenticated the request and the verifier produced
-- by the configured active key. This function locks exactly one live active credential,
-- authenticates it with the constant-work comparator introduced by migration 023, and
-- upgrades the verifier in the same transaction. Pending, revoked, expired, foreign,
-- and inactive-principal credentials cannot enter the update path.

BEGIN;

DO $contract_preflight$
DECLARE
    owner_role text;
    api_cap text;
BEGIN
    SELECT contract.owner_role,contract.api_capability_role
      INTO owner_role,api_cap
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();

    IF owner_role IS NULL OR api_cap IS NULL
       OR CURRENT_USER IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
              (SELECT relowner FROM pg_catalog.pg_class
                WHERE oid='public.installation_credentials'::pg_catalog.regclass))
              IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
              (SELECT proowner FROM pg_catalog.pg_proc
                WHERE oid='public.installation_verifier_matches(bytea,bytea)'::pg_catalog.regprocedure))
              IS DISTINCT FROM owner_role
       OR pg_catalog.to_regprocedure(
              'public.resolve_installation_and_rehash(bytea,smallint,bytea,smallint)') IS NOT NULL
       OR NOT pg_catalog.has_function_privilege(
              api_cap,'public.resolve_installation(bytea,smallint)','EXECUTE')
       OR NOT pg_catalog.has_function_privilege(
              api_cap,'public.resolve_installation_rotation_commit(uuid,bytea,smallint)','EXECUTE')
       OR pg_catalog.has_table_privilege(
              api_cap,'public.installation_credentials','INSERT,UPDATE,DELETE') THEN
        RAISE EXCEPTION 'installation credential rehash preflight rejected'
            USING ERRCODE='42501';
    END IF;
END;
$contract_preflight$;

CREATE FUNCTION public.resolve_installation_and_rehash(
    p_secret_hash bytea,
    p_key_version smallint,
    p_active_secret_hash bytea,
    p_active_key_version smallint)
RETURNS TABLE(
    principal_id uuid,
    credential_id uuid,
    generation integer,
    tier varchar,
    principal_status varchar,
    credential_state varchar)
LANGUAGE plpgsql
VOLATILE
SECURITY DEFINER
SET search_path=pg_catalog,pg_temp
AS $body$
DECLARE
    resolved_principal_id uuid;
    resolved_credential_id uuid;
    resolved_generation integer;
    resolved_tier varchar(20);
    resolution_time timestamptz:=pg_catalog.statement_timestamp();
BEGIN
    IF p_key_version IS NULL OR p_key_version<=0
       OR p_active_key_version IS NULL OR p_active_key_version<=0
       OR p_active_key_version<p_key_version
       OR p_secret_hash IS NULL OR pg_catalog.octet_length(p_secret_hash)<>32
       OR p_active_secret_hash IS NULL
       OR pg_catalog.octet_length(p_active_secret_hash)<>32
       OR p_secret_hash=pg_catalog.decode(pg_catalog.repeat('00',32),'hex')
       OR p_active_secret_hash=pg_catalog.decode(pg_catalog.repeat('00',32),'hex')
       OR (p_active_key_version=p_key_version
           AND NOT public.installation_verifier_matches(
               p_secret_hash,p_active_secret_hash)) THEN
        RAISE EXCEPTION 'invalid installation credential rehash input'
            USING ERRCODE='22023';
    END IF;

    -- Steady-state authentication is read-only and uses the unique
    -- (hash_key_version,secret_hash) verifier index. Constant-time comparison is
    -- retained as a defense-in-depth check on the single selected row; it must
    -- never replace the sargable equality predicate.
    IF p_key_version=p_active_key_version THEN
        RETURN QUERY
        SELECT principal.id,credential.id,credential.generation,principal.tier,
               'active'::varchar,'active'::varchar
          FROM public.installation_credentials credential
          JOIN public.users principal ON principal.id=credential.principal_id
         WHERE credential.hash_key_version=p_key_version
           AND credential.secret_hash=p_secret_hash
           AND public.installation_verifier_matches(
                   credential.secret_hash,p_secret_hash)
           AND credential.state='active'
           AND credential.activated_at<=resolution_time
           AND credential.pending_expires_at IS NULL
           AND (credential.expires_at IS NULL
                OR credential.expires_at>resolution_time)
           AND principal.principal_status='active'
           AND (principal.principal_expires_at IS NULL
                OR principal.principal_expires_at>resolution_time);
        RETURN;
    END IF;

    -- Only a credential that actually needs rehashing takes a row lock.
    SELECT principal.id,credential.id,credential.generation,principal.tier
      INTO resolved_principal_id,resolved_credential_id,
           resolved_generation,resolved_tier
      FROM public.installation_credentials credential
      JOIN public.users principal ON principal.id=credential.principal_id
     WHERE credential.hash_key_version=p_key_version
       AND credential.secret_hash=p_secret_hash
       AND public.installation_verifier_matches(
               credential.secret_hash,p_secret_hash)
       AND credential.state='active'
       AND credential.activated_at<=resolution_time
       AND credential.pending_expires_at IS NULL
       AND (credential.expires_at IS NULL
            OR credential.expires_at>resolution_time)
       AND principal.principal_status='active'
       AND (principal.principal_expires_at IS NULL
            OR principal.principal_expires_at>resolution_time)
     FOR UPDATE OF credential;

    IF resolved_credential_id IS NULL THEN
        -- A concurrent request may have authenticated the same old verifier and
        -- committed its upgrade while this request waited on the row lock. Re-read
        -- only the caller-supplied active verifier; this makes the operation
        -- idempotent without ever accepting a pending or otherwise non-live row.
        SELECT principal.id,credential.id,credential.generation,principal.tier
          INTO resolved_principal_id,resolved_credential_id,
               resolved_generation,resolved_tier
          FROM public.installation_credentials credential
          JOIN public.users principal ON principal.id=credential.principal_id
         WHERE credential.hash_key_version=p_active_key_version
           AND credential.secret_hash=p_active_secret_hash
           AND public.installation_verifier_matches(
                   credential.secret_hash,p_active_secret_hash)
           AND credential.state='active'
           AND credential.activated_at<=resolution_time
           AND credential.pending_expires_at IS NULL
           AND (credential.expires_at IS NULL
                OR credential.expires_at>resolution_time)
           AND principal.principal_status='active'
           AND (principal.principal_expires_at IS NULL
                OR principal.principal_expires_at>resolution_time);
        IF resolved_credential_id IS NULL THEN
            RETURN;
        END IF;

        RETURN QUERY SELECT resolved_principal_id,resolved_credential_id,
            resolved_generation,resolved_tier,'active'::varchar,'active'::varchar;
        RETURN;
    END IF;

    UPDATE public.installation_credentials credential
       SET secret_hash=p_active_secret_hash,
           hash_key_version=p_active_key_version
     WHERE credential.id=resolved_credential_id
       AND credential.hash_key_version=p_key_version
       AND credential.secret_hash=p_secret_hash
       AND public.installation_verifier_matches(
               credential.secret_hash,p_secret_hash)
       AND credential.state='active';
    IF NOT FOUND THEN
        RETURN;
    END IF;

    RETURN QUERY SELECT resolved_principal_id,resolved_credential_id,
        resolved_generation,resolved_tier,'active'::varchar,'active'::varchar;
END;
$body$;

DO $function_ownership_and_acl$
DECLARE
    owner_role text;
    api_cap text;
BEGIN
    SELECT contract.owner_role,contract.api_capability_role
      INTO owner_role,api_cap
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();

    ALTER FUNCTION public.resolve_installation_and_rehash(
        bytea,smallint,bytea,smallint) OWNER TO CURRENT_USER;
    REVOKE ALL ON FUNCTION public.resolve_installation_and_rehash(
        bytea,smallint,bytea,smallint) FROM PUBLIC;
    EXECUTE pg_catalog.format(
        'REVOKE ALL ON FUNCTION public.resolve_installation_and_rehash(bytea,smallint,bytea,smallint) FROM %I',
        api_cap);
    EXECUTE pg_catalog.format(
        'GRANT EXECUTE ON FUNCTION public.resolve_installation_and_rehash(bytea,smallint,bytea,smallint) TO %I',
        api_cap);

    IF pg_catalog.pg_get_userbyid(
           (SELECT proowner FROM pg_catalog.pg_proc
             WHERE oid='public.resolve_installation_and_rehash(bytea,smallint,bytea,smallint)'::pg_catalog.regprocedure))
           IS DISTINCT FROM owner_role
       OR (SELECT pg_catalog.count(*)=1
                  AND pg_catalog.bool_and(
                      grantee.rolname=api_cap
                      AND grantor.rolname=owner_role
                      AND acl.privilege_type='EXECUTE'
                      AND NOT acl.is_grantable)
             FROM pg_catalog.pg_proc function_row
             CROSS JOIN LATERAL pg_catalog.aclexplode(
                 COALESCE(function_row.proacl,
                     pg_catalog.acldefault('f',function_row.proowner))) acl
             LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
             LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
            WHERE function_row.oid=
                  'public.resolve_installation_and_rehash(bytea,smallint,bytea,smallint)'::pg_catalog.regprocedure
              AND acl.grantee<>function_row.proowner) IS NOT TRUE
       OR pg_catalog.has_table_privilege(
              api_cap,'public.installation_credentials','INSERT,UPDATE,DELETE') THEN
        RAISE EXCEPTION 'installation credential rehash function owner rejected'
            USING ERRCODE='42501';
    END IF;
END;
$function_ownership_and_acl$;

COMMENT ON FUNCTION public.resolve_installation_and_rehash(
    bytea,smallint,bytea,smallint) IS
    'Authenticates one live active installation credential and atomically upgrades an old accepted verifier to the API-configured active HMAC key version.';

-- Migration 017 broadened this legacy projection from literal public holidays to
-- every authoritative calendar day on which an observation is not expected. Keep
-- the live catalog description aligned with that backward-compatible contract.
COMMENT ON TABLE public.market_holidays IS
    'Legacy contract-v1 projection of authoritative calendar days where observation_expected is false; ingestion does not classify those dates as missing data.';

COMMIT;

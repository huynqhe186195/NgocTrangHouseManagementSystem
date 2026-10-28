-- Slice A / READ-ONLY EFFECTIVE RUNTIME-ROLE ATTESTATION
-- Execute AS the application runtime LOGIN on the intended Building DB,
-- with its normal search_path. NEVER use migration/admin credentials.
-- ONE SELECT only: no SET ROLE, CREATE/ALTER/GRANT/REVOKE or DB mutations.
-- A missing table, trigger or permission returns FALSE (not PASS).
-- Check BOTH Identity + Building credentials independently in operational review.
WITH target AS (
    SELECT current_schema() AS schema_name,
           to_regclass(format('%I.%I', current_schema(),
               'BuildingManagementOperatorBuildingGrants')) AS grant_table,
           to_regclass(format('%I.%I', current_schema(),
               'BuildingManagementOwnerRecoverySignoffs')) AS recovery_table
), objects AS (
    SELECT t.*,
           grant_class.relowner AS grant_owner,
           recovery_class.relowner AS recovery_owner,
           namespace.nspowner AS schema_owner,
           EXISTS (
               SELECT 1
                 FROM pg_catalog.pg_trigger tr
                 JOIN pg_catalog.pg_proc fn ON fn.oid = tr.tgfoid
                WHERE tr.tgrelid = t.grant_table
                  AND tr.tgname = 'TR_BM_OperatorGrant_RevocationOnly'
                  AND tr.tgenabled IN ('O', 'A')
                  AND NOT pg_has_role(current_user, fn.proowner, 'MEMBER')
           ) AS grant_guard_live,
           EXISTS (
               SELECT 1
                 FROM pg_catalog.pg_trigger tr
                 JOIN pg_catalog.pg_proc fn ON fn.oid = tr.tgfoid
                WHERE tr.tgrelid = t.recovery_table
                  AND tr.tgname = 'TR_BM_RecoverySignoff_ConsumeOnly'
                  AND tr.tgenabled IN ('O', 'A')
                  AND NOT pg_has_role(current_user, fn.proowner, 'MEMBER')
           ) AS recovery_guard_live
      FROM target t
      LEFT JOIN pg_catalog.pg_class grant_class
             ON grant_class.oid = t.grant_table
      LEFT JOIN pg_catalog.pg_class recovery_class
             ON recovery_class.oid = t.recovery_table
      LEFT JOIN pg_catalog.pg_namespace namespace
             ON namespace.nspname = t.schema_name
), role_info AS (
    SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole
                OR rolreplication OR rolbypassrls) AS no_admin_powers
      FROM pg_catalog.pg_roles
     WHERE rolname = current_user
), checks AS (
    SELECT current_user AS runtime_login,
           session_user AS session_login,
           o.schema_name,
           o.grant_table IS NOT NULL AS grant_table_present,
           o.recovery_table IS NOT NULL AS recovery_table_present,
           COALESCE(
               (SELECT no_admin_powers FROM role_info), FALSE)
               AND current_user = session_user
               AND o.schema_owner IS NOT NULL
               AND NOT pg_has_role(current_user, o.schema_owner, 'MEMBER')
               AND NOT COALESCE(has_schema_privilege(o.schema_name, 'CREATE'), TRUE)
               AS role_pass,
           o.grant_table IS NOT NULL
               AND o.grant_owner IS NOT NULL
               AND NOT pg_has_role(current_user, o.grant_owner, 'MEMBER')
               AND o.grant_guard_live
               AND COALESCE(has_table_privilege(o.grant_table, 'SELECT'), FALSE)
               AND COALESCE(has_table_privilege(o.grant_table, 'INSERT'), FALSE)
               AND COALESCE(has_column_privilege(o.grant_table, 'RevokedAtUtc', 'UPDATE'), FALSE)
               AND COALESCE(has_column_privilege(o.grant_table, 'RevokedByUserId', 'UPDATE'), FALSE)
               AND COALESCE(has_column_privilege(o.grant_table, 'RevisionNumber', 'UPDATE'), FALSE)
               AND NOT COALESCE(has_column_privilege(o.grant_table, 'OperatorUserId', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_column_privilege(o.grant_table, 'BuildingId', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_column_privilege(o.grant_table, 'GrantedByUserId', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_column_privilege(o.grant_table, 'GrantedAtUtc', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_column_privilege(o.grant_table, 'ExpiresAtUtc', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_table_privilege(o.grant_table, 'DELETE'), TRUE)
               AND NOT COALESCE(has_table_privilege(o.grant_table, 'TRUNCATE'), TRUE)
               AND NOT COALESCE(has_table_privilege(o.grant_table, 'TRIGGER'), TRUE)
               AS grant_pass,
           o.recovery_table IS NOT NULL
               AND o.recovery_owner IS NOT NULL
               AND NOT pg_has_role(current_user, o.recovery_owner, 'MEMBER')
               AND o.recovery_guard_live
               AND COALESCE(has_table_privilege(o.recovery_table, 'SELECT'), FALSE)
               AND COALESCE(has_table_privilege(o.recovery_table, 'INSERT'), FALSE)
               AND COALESCE(has_column_privilege(o.recovery_table, 'ConsumedAtUtc', 'UPDATE'), FALSE)
               AND COALESCE(has_column_privilege(o.recovery_table, 'RecoveryGrantId', 'UPDATE'), FALSE)
               AND NOT COALESCE(has_column_privilege(o.recovery_table, 'EvidenceId', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_column_privilege(o.recovery_table, 'ApprovedByUserId', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_column_privilege(o.recovery_table, 'BuildingId', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_column_privilege(o.recovery_table, 'NewOwnerId', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_column_privilege(o.recovery_table, 'ExpiresAtUtc', 'UPDATE'), TRUE)
               AND NOT COALESCE(has_table_privilege(o.recovery_table, 'DELETE'), TRUE)
               AND NOT COALESCE(has_table_privilege(o.recovery_table, 'TRUNCATE'), TRUE)
               AND NOT COALESCE(has_table_privilege(o.recovery_table, 'TRIGGER'), TRUE)
               AS recovery_pass
      FROM objects o
)
SELECT runtime_login, session_login, schema_name,
       grant_table_present, recovery_table_present,
       role_pass, grant_pass, recovery_pass,
       (role_pass AND grant_pass AND recovery_pass) AS slice_a_role_pass
  FROM checks;

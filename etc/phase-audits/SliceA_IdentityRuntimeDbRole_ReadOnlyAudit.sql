-- Slice A / IDENTITY DATABASE READ-ONLY RUNTIME LOGIN ATTESTATION
-- Run ONLY as the actual Identity app runtime LOGIN on the intended Identity
-- PostgreSQL DB. Do not run as migration/admin role. No SET ROLE, DDL or writes.
-- Checks effective privileges (including inherited role grants), not just ACL.
-- SQL is one SELECT; it never provisions, revokes or changes Identity records.
WITH target AS (
    SELECT current_schema() AS schema_name,
           to_regclass(format('%I.%I', current_schema(), 'AbpUsers')) AS users_table,
           to_regclass(format('%I.%I', current_schema(), 'AbpRoles')) AS roles_table,
           to_regclass(format('%I.%I', current_schema(), 'AbpUserRoles')) AS user_roles_table,
           to_regclass(format('%I.%I', current_schema(), 'AbpPermissionGrants')) AS permission_table
), check_schema AS (
    SELECT t.*,
           ns.nspowner AS schema_owner,
           us.relowner AS user_owner,
           ro.relowner AS role_owner,
           ur.relowner AS membership_owner,
           pe.relowner AS permission_owner,
           NOT COALESCE(has_database_privilege(current_database(), 'CREATE'), TRUE)
               AS cannot_create_database_objects
      FROM target t
      LEFT JOIN pg_catalog.pg_namespace ns ON ns.nspname = t.schema_name
      LEFT JOIN pg_catalog.pg_class us ON us.oid = t.users_table
      LEFT JOIN pg_catalog.pg_class ro ON ro.oid = t.roles_table
      LEFT JOIN pg_catalog.pg_class ur ON ur.oid = t.user_roles_table
      LEFT JOIN pg_catalog.pg_class pe ON pe.oid = t.permission_table
), role_info AS (
    SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole
                OR rolreplication OR rolbypassrls) AS no_admin_powers
      FROM pg_catalog.pg_roles WHERE rolname = current_user
), checks AS (
    SELECT current_user AS runtime_login,
           session_user AS session_login,
           current_database() AS database_name,
           c.schema_name,
           c.users_table IS NOT NULL
             AND c.roles_table IS NOT NULL
             AND c.user_roles_table IS NOT NULL
             AND c.permission_table IS NOT NULL AS identity_tables_present,
           COALESCE((SELECT no_admin_powers FROM role_info), FALSE)
             AND current_user = session_user
             AND c.schema_owner IS NOT NULL
             AND NOT pg_has_role(current_user, c.schema_owner, 'MEMBER')
             AND c.cannot_create_database_objects
             AND NOT COALESCE(has_schema_privilege(c.schema_name, 'CREATE'), TRUE)
             AS role_pass,
           c.users_table IS NOT NULL
             AND c.user_owner IS NOT NULL
             AND NOT pg_has_role(current_user, c.user_owner, 'MEMBER')
             AND COALESCE(has_table_privilege(c.users_table, 'SELECT'), FALSE)
             AND NOT COALESCE(has_table_privilege(c.users_table, 'TRUNCATE'), TRUE)
             AND NOT COALESCE(has_table_privilege(c.users_table, 'TRIGGER'), TRUE)
             AS users_pass,
           c.roles_table IS NOT NULL
             AND c.role_owner IS NOT NULL
             AND NOT pg_has_role(current_user, c.role_owner, 'MEMBER')
             AND COALESCE(has_table_privilege(c.roles_table, 'SELECT'), FALSE)
             AND NOT COALESCE(has_table_privilege(c.roles_table, 'TRUNCATE'), TRUE)
             AND NOT COALESCE(has_table_privilege(c.roles_table, 'TRIGGER'), TRUE)
             AS roles_pass,
           c.user_roles_table IS NOT NULL
             AND c.membership_owner IS NOT NULL
             AND NOT pg_has_role(current_user, c.membership_owner, 'MEMBER')
             AND COALESCE(has_table_privilege(c.user_roles_table, 'SELECT'), FALSE)
             AND NOT COALESCE(has_table_privilege(c.user_roles_table, 'TRUNCATE'), TRUE)
             AND NOT COALESCE(has_table_privilege(c.user_roles_table, 'TRIGGER'), TRUE)
             AS membership_pass,
           c.permission_table IS NOT NULL
             AND c.permission_owner IS NOT NULL
             AND NOT pg_has_role(current_user, c.permission_owner, 'MEMBER')
             AND COALESCE(has_table_privilege(c.permission_table, 'SELECT'), FALSE)
             AND NOT COALESCE(has_table_privilege(c.permission_table, 'TRUNCATE'), TRUE)
             AND NOT COALESCE(has_table_privilege(c.permission_table, 'TRIGGER'), TRUE)
             AS permission_pass
      FROM check_schema c
)
SELECT runtime_login, session_login, database_name, schema_name,
       identity_tables_present, role_pass,
       users_pass, roles_pass, membership_pass, permission_pass,
       (identity_tables_present AND role_pass AND users_pass
        AND roles_pass AND membership_pass AND permission_pass)
           AS slice_a_identity_role_pass
  FROM checks;

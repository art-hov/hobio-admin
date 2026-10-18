/*
    Hobio Admin: read-only database user for the portal.

    Variables (replaced by the DbTool, or use sqlcmd -v):
      $(ReaderLogin)      login/user name, default hobio_admin_reader
      $(ReaderPassword)   strong password (store it only in AWS Secrets Manager)
      $(DatabaseName)     the Tushpa database name

    Part 1 runs in [master] (creates the server login), part 2 in the Tushpa database.
    The DbTool runs each part against the right database. Safe to re-run; the password is reset each time.

    The user can SELECT from the [analytics] schema only. It has no access to [dbo] tables:
    the views read [dbo] through ownership chaining (both schemas are owned by dbo).
*/

-- ==== PART 1: master ====
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'$(ReaderLogin)')
    CREATE LOGIN [$(ReaderLogin)] WITH PASSWORD = N'$(ReaderPassword)', CHECK_POLICY = ON, DEFAULT_DATABASE = [$(DatabaseName)];
ELSE
    ALTER LOGIN [$(ReaderLogin)] WITH PASSWORD = N'$(ReaderPassword)';
GO

-- ==== PART 2: Tushpa database ====
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(ReaderLogin)')
    CREATE USER [$(ReaderLogin)] FOR LOGIN [$(ReaderLogin)] WITH DEFAULT_SCHEMA = [analytics];
GO

GRANT SELECT ON SCHEMA::[analytics] TO [$(ReaderLogin)];
GO

DENY SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES, VIEW DEFINITION ON SCHEMA::[dbo] TO [$(ReaderLogin)];
GO

DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER ON SCHEMA::[analytics] TO [$(ReaderLogin)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_ID(N'CatalogDB') IS NULL THROW 50000, 'Isolated CatalogDB is required', 1;
DECLARE @suffix nvarchar(32) = REPLACE(CONVERT(nvarchar(36), NEWID()), '-', '');
DECLARE @target sysname = N'RestoreVerification_' + @suffix;
DECLARE @backup nvarchar(4000) = N'/var/opt/mssql/data/restore-verification-' + @suffix + N'.bak';
BACKUP DATABASE [CatalogDB] TO DISK = @backup WITH COPY_ONLY, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = @backup WITH CHECKSUM;
DECLARE @moves nvarchar(max);
SELECT @moves = STRING_AGG(CAST(N', MOVE N' + QUOTENAME(name, '''') + N' TO N' +
    QUOTENAME(N'/var/opt/mssql/data/' + @target + N'-' + CONVERT(nvarchar(10), file_id) +
        CASE WHEN type = 1 THEN N'.ldf' ELSE N'.mdf' END, '''') AS nvarchar(max)), N'')
FROM sys.master_files WHERE database_id = DB_ID(N'CatalogDB');
DECLARE @sql nvarchar(max) = N'RESTORE DATABASE ' + QUOTENAME(@target) + N' FROM DISK = N' +
    QUOTENAME(@backup, '''') + N' WITH CHECKSUM' + @moves + N';';
EXEC sp_executesql @sql;
SET @sql = N'DBCC CHECKDB (' + QUOTENAME(@target) + N') WITH NO_INFOMSGS;
IF EXISTS (
    SELECT s.name, t.name, p.rows FROM CatalogDB.sys.tables t
    JOIN CatalogDB.sys.schemas s ON s.schema_id=t.schema_id
    JOIN CatalogDB.sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)
    EXCEPT
    SELECT s.name, t.name, p.rows FROM ' + QUOTENAME(@target) + N'.sys.tables t
    JOIN ' + QUOTENAME(@target) + N'.sys.schemas s ON s.schema_id=t.schema_id
    JOIN ' + QUOTENAME(@target) + N'.sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)
) THROW 50001, ''Restored table row counts differ'', 1;';
EXEC sp_executesql @sql;
SELECT @target AS restored_database, @backup AS backup_path, SYSUTCDATETIME() AS verified_at_utc;
PRINT 'PASS actual SQL restore, CHECKDB and source table row counts (quiescent test data required)';

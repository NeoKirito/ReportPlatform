/*
  PEIS Legacy Report Schema Evidence Collector
  Read-only: this script uses only SELECT statements against sys.* metadata.
  Run in SSMS against an explicitly approved read-only legacy report database.
  Do not paste result sets containing patient or credential data into source control.
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @Tables TABLE (TableName sysname NOT NULL PRIMARY KEY);
INSERT INTO @Tables (TableName) VALUES (N'xt_bbdy'), (N'xt_djwh'), (N'xt_cxdy'), (N'xt_bgdy_djwh_zzj');

/* 1. Database identity and server clock. */
SELECT DB_NAME() AS DatabaseName, @@SERVERNAME AS ServerName, SYSUTCDATETIME() AS CollectedAtUtc;

/* 2. Tables and estimated row counts. A missing table remains visible as NOT FOUND. */
SELECT
    Expected.TableName AS ExpectedTable,
    s.name AS SchemaName,
    t.name AS ActualTable,
    CASE WHEN t.object_id IS NULL THEN N'NOT FOUND' ELSE N'FOUND' END AS Presence,
    COALESCE(SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count END), 0) AS RowCount
FROM @Tables AS Expected
LEFT JOIN sys.tables AS t ON t.name = Expected.TableName
LEFT JOIN sys.schemas AS s ON s.schema_id = t.schema_id
LEFT JOIN sys.dm_db_partition_stats AS ps ON ps.object_id = t.object_id
GROUP BY Expected.TableName, s.name, t.name, t.object_id
ORDER BY Expected.TableName;

/* 3. Columns, SQL types, nullability, identity/computed markers, and rowversion candidates. */
SELECT
    s.name AS SchemaName,
    t.name AS TableName,
    c.column_id AS Ordinal,
    c.name AS ColumnName,
    ty.name AS SqlType,
    c.max_length AS MaxLengthBytes,
    c.precision AS NumericPrecision,
    c.scale AS NumericScale,
    c.is_nullable AS IsNullable,
    c.is_identity AS IsIdentity,
    c.is_computed AS IsComputed,
    CASE WHEN ty.name IN (N'timestamp', N'rowversion') THEN 1 ELSE 0 END AS IsRowVersion
FROM sys.tables AS t
INNER JOIN @Tables AS Expected ON Expected.TableName = t.name
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
INNER JOIN sys.columns AS c ON c.object_id = t.object_id
INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
ORDER BY t.name, c.column_id;

/* 4. Primary keys, unique constraints, and ordinary indexes with their key columns. */
SELECT
    s.name AS SchemaName,
    t.name AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_primary_key AS IsPrimaryKey,
    i.is_unique AS IsUnique,
    ic.key_ordinal AS KeyOrdinal,
    c.name AS ColumnName
FROM sys.tables AS t
INNER JOIN @Tables AS Expected ON Expected.TableName = t.name
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
INNER JOIN sys.indexes AS i ON i.object_id = t.object_id
INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.index_id > 0
  AND ic.is_included_column = 0
ORDER BY t.name, i.name, ic.key_ordinal;

/* 5. Foreign-key relationships where either side is one of the expected report tables. */
SELECT
    fk.name AS ForeignKeyName,
    ps.name AS ParentSchema,
    pt.name AS ParentTable,
    pc.name AS ParentColumn,
    rs.name AS ReferencedSchema,
    rt.name AS ReferencedTable,
    rc.name AS ReferencedColumn
FROM sys.foreign_keys AS fk
INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.tables AS pt ON pt.object_id = fk.parent_object_id
INNER JOIN sys.schemas AS ps ON ps.schema_id = pt.schema_id
INNER JOIN sys.columns AS pc ON pc.object_id = pt.object_id AND pc.column_id = fkc.parent_column_id
INNER JOIN sys.tables AS rt ON rt.object_id = fk.referenced_object_id
INNER JOIN sys.schemas AS rs ON rs.schema_id = rt.schema_id
INNER JOIN sys.columns AS rc ON rc.object_id = rt.object_id AND rc.column_id = fkc.referenced_column_id
WHERE pt.name IN (N'xt_bbdy', N'xt_djwh', N'xt_cxdy', N'xt_bgdy_djwh_zzj')
   OR rt.name IN (N'xt_bbdy', N'xt_djwh', N'xt_cxdy', N'xt_bgdy_djwh_zzj')
ORDER BY pt.name, fk.name, fkc.constraint_column_id;

/*
  Optional controlled one-record sampling. Keep the query parameterized and replace only the DECLARE value.
  Do not export result sets with patient data. The CLI tool records only field fingerprints by default.

  DECLARE @bbid nvarchar(256) = N'<approved single report id>';
  SELECT TOP (1) * FROM dbo.xt_bbdy WHERE bbid = @bbid;
*/

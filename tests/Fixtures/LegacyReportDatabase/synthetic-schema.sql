-- SYNTHETIC FIXTURE ONLY. This schema is not asserted to match production PEIS.
-- It exists to make the expected database-driven report definition contract executable once SQL Server is available.

CREATE TABLE dbo.xt_bbdy
(
    bbid nvarchar(64) NOT NULL PRIMARY KEY,
    bb_frx nvarchar(max) NOT NULL,
    bb_sql nvarchar(max) NOT NULL,
    definition_version bigint NOT NULL,
    updated_at datetime2 NOT NULL
);

CREATE TABLE dbo.xt_djwh
(
    djid nvarchar(64) NOT NULL PRIMARY KEY,
    djsql nvarchar(max) NULL,
    dj_frx nvarchar(max) NULL,
    definition_version bigint NOT NULL,
    updated_at datetime2 NOT NULL
);

CREATE TABLE dbo.xt_cxdy
(
    cxid nvarchar(64) NOT NULL PRIMARY KEY,
    query_sql nvarchar(max) NULL,
    definition_version bigint NOT NULL,
    updated_at datetime2 NOT NULL
);

CREATE TABLE dbo.xt_bgdy_djwh_zzj
(
    bbid nvarchar(64) NOT NULL,
    djid nvarchar(64) NOT NULL,
    sort_order int NOT NULL DEFAULT 0,
    CONSTRAINT PK_xt_bgdy_djwh_zzj PRIMARY KEY (bbid, djid)
);
GO

INSERT INTO dbo.xt_bbdy (bbid, bb_frx, bb_sql, definition_version, updated_at)
VALUES
(
    N'GUIDE_A4',
    N'<Report ScriptLanguage="CSharp"><Dictionary /></Report>',
    N'SELECT CAST(@tjh AS nvarchar(64)) AS tjh, CAST(N''GUIDE'' AS nvarchar(16)) AS report_kind;',
    1,
    '2026-08-14T00:00:00'
);
GO

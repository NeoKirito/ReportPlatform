using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Infrastructure.SqlServer;

/// <summary>
/// 报表数据库选项配置。
/// </summary>
public sealed class ReportDatabaseOptions
{
    /// <summary>数据库提供者，目前仅支持"SqlServer"</summary>
    public string Provider { get; set; } = "SqlServer";
    /// <summary>数据库连接字符串</summary>
    public string ConnectionString { get; set; } = string.Empty;
    /// <summary>SQL命令超时（秒），默认30秒</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
    /// <summary>定义缓存TTL（秒），默认3600秒（1小时）</summary>
    public int DefinitionCacheTtlSeconds { get; set; } = 3600;
}

/// <summary>
/// 旧版报表Schema映射配置。
/// 定义数据库表/列名与报表定义的对应关系。
/// 
/// 默认值是从PEIS数据库实际表结构推断的占位符。
/// 每个字段都必须通过Validate()验证，确保是合法的SQL标识符。
/// </summary>
public sealed class LegacyReportSchemaMapping
{
    /// <summary>报表定义表名，默认"xt_bbdy"</summary>
    public string DefinitionTable { get; set; } = "xt_bbdy";
    /// <summary>报表ID列名，默认"bbid"</summary>
    public string ReportIdColumn { get; set; } = "bbid";
    /// <summary>FRX模板列名，默认"bb_frx"</summary>
    public string TemplateColumn { get; set; } = "bb_frx";
    /// <summary>SQL查询列名，默认"bb_sql"</summary>
    public string SqlColumn { get; set; } = "bb_sql";
    /// <summary>版本列名（可选），用于缓存失效</summary>
    public string? VersionColumn { get; set; }
    /// <summary>更新时间列名（可选），用于缓存失效</summary>
    public string? UpdatedAtColumn { get; set; }
    /// <summary>
    /// 模板内容编码方式：
    /// - "Raw"：直接存储XML文本
    /// - "Base64Utf8"：Base64编码的UTF-8 FRX XML
    /// </summary>
    public string TemplateContentEncoding { get; set; } = "Raw";
    /// <summary>第一个SQL结果集的DataTable名称（可选），FRX中可能引用"Master"</summary>
    public string? FirstResultSetTableName { get; set; }
    /// <summary>
    /// 补充查询排序列名（可选）。
    /// 用于加载以reportId_开头的子报表SQL（Master1、Master2等）。
    /// </summary>
    public string? SupplementalQueryOrderColumn { get; set; }
    /// <summary>报表名称列名（可选），用于按名称查找报表</summary>
    public string? ReportNameColumn { get; set; } = "djmc";
    /// <summary>模板缓存键前缀，默认"legacy-db"</summary>
    public string TemplateKeyPrefix { get; set; } = "legacy-db";

    /// <summary>
    /// 验证所有配置项是否为合法的SQL标识符。
    /// 阻止SQL注入攻击。
    /// </summary>
    public void Validate()
    {
        ValidateIdentifier(DefinitionTable, nameof(DefinitionTable));
        ValidateIdentifier(ReportIdColumn, nameof(ReportIdColumn));
        ValidateIdentifier(TemplateColumn, nameof(TemplateColumn));
        ValidateIdentifier(SqlColumn, nameof(SqlColumn));
        if (!string.IsNullOrWhiteSpace(VersionColumn)) ValidateIdentifier(VersionColumn, nameof(VersionColumn));
        if (!string.IsNullOrWhiteSpace(UpdatedAtColumn)) ValidateIdentifier(UpdatedAtColumn, nameof(UpdatedAtColumn));
        if (!string.IsNullOrWhiteSpace(ReportNameColumn)) ValidateIdentifier(ReportNameColumn, nameof(ReportNameColumn));
        if (!string.IsNullOrWhiteSpace(SupplementalQueryOrderColumn)) ValidateIdentifier(SupplementalQueryOrderColumn, nameof(SupplementalQueryOrderColumn));
        if (!string.Equals(TemplateContentEncoding, "Raw", StringComparison.OrdinalIgnoreCase) && !string.Equals(TemplateContentEncoding, "Base64Utf8", StringComparison.OrdinalIgnoreCase))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.SchemaMappingUnverified, "Legacy schema mapping option 'TemplateContentEncoding' must be Raw or Base64Utf8.");
    }

    /// <summary>验证SQL标识符：只允许字母、数字、下划线、点号</summary>
    private static void ValidateIdentifier(string value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '.')))
            throw new LegacyReportDatabaseException(
                LegacyReportDatabaseErrorCode.SchemaMappingUnverified,
                $"Legacy schema mapping option '{optionName}' must be a SQL identifier composed of letters, digits, underscore, or dot.");
    }
}

/// <summary>
/// 旧版数据库报表定义提供者 - 从SQL Server加载报表定义。
/// 
/// 职责：
/// 1. 从数据库表加载报表定义（FRX模板、SQL查询）
/// 2. 解析报表ID（支持多种查找方式）
/// 3. 加载补充查询（子报表）
/// 4. 管理定义缓存
/// 5. 提供报表目录列表
/// 
/// 数据库表结构（PEIS）：
/// - 主表：dbo.xt_bgdy_djwh_zzj
///   - djid: 报表ID
///   - djmc: 报表名称
///   - dj_frx: FRX模板（Base64编码）
///   - djsql: SQL查询语句
/// 
/// - 模板映射表：dbo.pe_xtcs_bgmb
///   - bgurl: 报表URL标识
///   - bgmbid: 模板ID
///   - bgmbmc: 模板名称
///   - BGID: 对应的报表ID
/// </summary>
public sealed class LegacyDatabaseReportDefinitionProvider : IReportDefinitionProvider, IReportDefinitionVersionProvider, IReportCatalogProvider
{
    private readonly ReportDatabaseOptions _database;
    private readonly LegacyReportSchemaMapping _schema;
    private readonly ILegacyReportResolver _resolver;
    private readonly TimeProvider _clock;

    /// <summary>报表ID解析缓存（避免重复查询数据库）</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resolvedIdCache = new(StringComparer.OrdinalIgnoreCase);

    public LegacyDatabaseReportDefinitionProvider(
        IOptions<ReportDatabaseOptions> database,
        IOptions<LegacyReportSchemaMapping> schema,
        TimeProvider? clock = null,
        ILegacyReportResolver? resolver = null)
    {
        _database = database.Value;
        _schema = schema.Value;
        _resolver = resolver ?? new LegacyPayloadReportResolver();
        _clock = clock ?? TimeProvider.System;
        _schema.Validate();
    }

    /// <summary>
    /// 解析实际报表ID：
    /// 旧版系统可能传入不同的标识符（报表ID、模板ID、报表名称等），
    /// 需要解析为数据库中的实际报表ID。
    /// 
    /// 查找顺序：
    /// 1. 直接匹配DefinitionTable（djid或djmc）
    /// 2. 查找pe_xtcs_bgmb表（通过bgurl、bgmbid或bgmbmc）
    /// 3. 如果提供了fileName，按名称查找
    /// 
    /// 结果缓存在内存中，避免重复查询。
    /// </summary>
    private async Task<string> ResolveActualReportIdAsync(SqlConnection connection, string inputId, string? fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inputId))
            return inputId;

        // 检查缓存
        if (_resolvedIdCache.TryGetValue(inputId, out var cached))
            return cached;

        // 策略1：直接匹配DefinitionTable（djid或djmc）
        var nameCondition = string.IsNullOrWhiteSpace(_schema.ReportNameColumn)
            ? string.Empty
            : $" OR {_schema.ReportNameColumn} = @id";
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandTimeout = TimeoutSeconds();
            cmd.CommandText = $"SELECT TOP 1 {_schema.ReportIdColumn} FROM {_schema.DefinitionTable} WHERE {_schema.ReportIdColumn} = @id{nameCondition} ORDER BY CASE WHEN {_schema.ReportIdColumn} = @id THEN 0 ELSE 1 END";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 128) { Value = inputId });
            var direct = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (direct is not null && direct is not DBNull)
            {
                var resolvedDirect = Convert.ToString(direct, System.Globalization.CultureInfo.InvariantCulture)!;
                _resolvedIdCache[inputId] = resolvedDirect;
                return resolvedDirect;
            }
        }

        // 策略2：查找pe_xtcs_bgmb表（通过bgurl、bgmbid或bgmbmc）
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = TimeoutSeconds();
            cmd.CommandText = """
                SELECT TOP 1 BGID 
                FROM dbo.pe_xtcs_bgmb 
                WHERE (bgurl = @id OR bgmbid = @id OR bgmbmc = @id) 
                  AND BGID IS NOT NULL AND BGID <> ''
                """;
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 128) { Value = inputId });
            var mapped = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (mapped is not null && mapped is not DBNull)
            {
                var resolvedMapped = Convert.ToString(mapped, System.Globalization.CultureInfo.InvariantCulture)!.Trim();
                if (!string.IsNullOrEmpty(resolvedMapped))
                {
                    _resolvedIdCache[inputId] = resolvedMapped;
                    return resolvedMapped;
                }
            }
        }
        catch (SqlException)
        {
            // pe_xtcs_bgmb表可能不存在，忽略并继续
        }

        // 策略3：如果提供了fileName（如"个人费用单据"），按名称查找
        if (!string.IsNullOrWhiteSpace(fileName) && !string.Equals(fileName, inputId, StringComparison.OrdinalIgnoreCase))
        {
            // 先查pe_xtcs_bgmb表
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandTimeout = TimeoutSeconds();
                cmd.CommandText = """
                    SELECT TOP 1 BGID 
                    FROM dbo.pe_xtcs_bgmb 
                    WHERE bgmbmc = @fn 
                      AND BGID IS NOT NULL AND BGID <> ''
                    """;
                cmd.Parameters.Add(new SqlParameter("@fn", SqlDbType.NVarChar, 128) { Value = fileName.Trim() });
                var mapped = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (mapped is not null && mapped is not DBNull)
                {
                    var resolvedMapped = Convert.ToString(mapped, System.Globalization.CultureInfo.InvariantCulture)!.Trim();
                    if (!string.IsNullOrEmpty(resolvedMapped))
                    {
                        _resolvedIdCache[inputId] = resolvedMapped;
                        return resolvedMapped;
                    }
                }
            }
            catch (SqlException) { }

            // 再查DefinitionTable的ReportNameColumn
            if (!string.IsNullOrWhiteSpace(_schema.ReportNameColumn))
            {
                try
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandTimeout = TimeoutSeconds();
                    cmd.CommandText = $"SELECT TOP 1 {_schema.ReportIdColumn} FROM {_schema.DefinitionTable} WHERE {_schema.ReportNameColumn} = @fn";
                    cmd.Parameters.Add(new SqlParameter("@fn", SqlDbType.NVarChar, 128) { Value = fileName.Trim() });
                    var direct = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (direct is not null && direct is not DBNull)
                    {
                        var resolvedDirect = Convert.ToString(direct, System.Globalization.CultureInfo.InvariantCulture)!;
                        _resolvedIdCache[inputId] = resolvedDirect;
                        return resolvedDirect;
                    }
                }
                catch (SqlException) { }
            }
        }

        return inputId;
    }

    /// <summary>
    /// 列出所有可用的报表ID：
    /// 1. 从DefinitionTable查询有FRX模板的报表
    /// 2. 从pe_xtcs_bgmb查询FastReport模板（dygs='2'）
    /// 
    /// 用于报表预热和目录展示。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListReportIdsAsync(CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var connection = new SqlConnection(_database.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 查询DefinitionTable中有FRX模板的报表
            var templateCondition = string.IsNullOrWhiteSpace(_schema.TemplateColumn)
                ? string.Empty
                : $" WHERE {_schema.TemplateColumn} IS NOT NULL AND {_schema.TemplateColumn} <> ''";

            await using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = TimeoutSeconds();
                command.CommandText = $"SELECT DISTINCT {_schema.ReportIdColumn} FROM {_schema.DefinitionTable}{templateCondition} ORDER BY {_schema.ReportIdColumn}";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    {
                        results.Add(id);
                    }
                }
            }

            // 查询pe_xtcs_bgmb表中的FastReport模板（dygs='2'）
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = TimeoutSeconds();
                command.CommandText = """
                    SELECT DISTINCT bgurl 
                    FROM dbo.pe_xtcs_bgmb 
                    WHERE dygs = '2' AND bgurl IS NOT NULL AND bgurl <> ''
                    ORDER BY bgurl
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var bgurl = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(bgurl) && seen.Add(bgurl))
                    {
                        results.Add(bgurl);
                    }
                }
            }
            catch (SqlException)
            {
                // pe_xtcs_bgmb表可能不存在
            }
        }
        catch (Exception ex)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, $"Failed to query report catalog: {ex.Message}", ex);
        }

        return results;
    }

    /// <summary>
    /// 获取报表定义版本：用于缓存失效判断。
    /// 
    /// 如果配置了VersionColumn或UpdatedAtColumn，查询数据库中的实际值。
    /// 否则使用TTL时间窗口（DefinitionCacheTtlSeconds）。
    /// </summary>
    public async Task<ReportDefinitionVersion> GetVersionAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var resolution = _resolver.Resolve(request);
        var selected = VersionExpression();
        if (selected is null)
        {
            // 无版本列：使用TTL桶号作为缓存键
            var seconds = Math.Clamp(_database.DefinitionCacheTtlSeconds, 1, 86400);
            var now = _clock.GetUtcNow();
            var bucket = now.ToUnixTimeSeconds() / seconds;
            return new ReportDefinitionVersion($"ttl:{bucket}", false, now.AddSeconds(seconds), "ttl-fallback-unverified-schema");
        }

        try
        {
            await using var connection = new SqlConnection(_database.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var reportIdToUse = await ResolveActualReportIdAsync(connection, resolution.DefinitionId, request.FileName, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = TimeoutSeconds();
            var nameCondition = string.IsNullOrWhiteSpace(_schema.ReportNameColumn)
                ? string.Empty
                : $" OR {_schema.ReportNameColumn} = @reportId";
            command.CommandText = $"SELECT TOP 1 {selected} FROM {_schema.DefinitionTable} WHERE {_schema.ReportIdColumn} = @reportId{nameCondition} ORDER BY CASE WHEN {_schema.ReportIdColumn} = @reportId THEN 0 ELSE 1 END";
            command.Parameters.Add(new SqlParameter("@reportId", SqlDbType.NVarChar, 128) { Value = reportIdToUse });
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is null || scalar is DBNull)
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.ReportNotFound, $"Legacy report definition '{resolution.DefinitionId}' was not found.");
            return new ReportDefinitionVersion(Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture)!, true, null, selected);
        }
        catch (LegacyReportDatabaseException)
        {
            throw;
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseTimeout, "Timed out checking the legacy report definition version.", exception);
        }
        catch (SqlException exception)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, "Unable to check the legacy report definition version.", exception);
        }
    }

    /// <summary>
    /// 加载报表定义（核心方法）：
    /// 1. 解析报表ID（支持多种查找方式）
    /// 2. 查询DefinitionTable获取FRX模板和SQL
    /// 3. 加载补充查询（子报表SQL）
    /// 4. 计算版本号（优先使用数据库版本列，否则用SHA256哈希）
    /// 5. 返回ReportDefinition对象
    /// </summary>
    public async Task<ReportDefinition> GetRequiredAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var resolution = _resolver.Resolve(request);
        try
        {
            await using var connection = new SqlConnection(_database.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var reportIdToUse = await ResolveActualReportIdAsync(connection, resolution.DefinitionId, request.FileName, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = TimeoutSeconds();
            command.CommandText = BuildDefinitionQuery();
            command.Parameters.Add(new SqlParameter("@reportId", SqlDbType.NVarChar, 128) { Value = reportIdToUse });
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.ReportNotFound, $"Legacy report definition '{resolution.DefinitionId}' was not found.");

            var actualReportId = reader.GetString(0);
            var template = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(template))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.TemplateNotFound, $"Legacy report definition '{actualReportId}' has no FRX content.");
            var sql = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(sql))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.QueryDefinitionNotFound, $"Legacy report definition '{actualReportId}' has no SQL definition.");

            var storedVersion = reader.IsDBNull(3)
                ? null
                : Convert.ToString(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)!;
            var updatedAt = reader.IsDBNull(4) ? _clock.GetUtcNow() : new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc));
            await reader.DisposeAsync().ConfigureAwait(false);

            // 加载补充查询（子报表SQL）
            var supplementalQueries = await LoadSupplementalQueriesAsync(connection, actualReportId, cancellationToken).ConfigureAwait(false);

            // 计算版本号：优先使用数据库版本列，否则用SHA256哈希
            var versionMaterial = string.Join("\n", supplementalQueries.Select(query => query.SqlText).Prepend(sql).Prepend(template));
            var version = storedVersion ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionMaterial)));

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["schemaMapping"] = _schema.DefinitionTable,
                ["identifierSource"] = resolution.IdentifierSource,
                ["templateContentEncoding"] = _schema.TemplateContentEncoding
            };
            if (!string.IsNullOrWhiteSpace(_schema.FirstResultSetTableName))
                metadata["resultSet:0:tableName"] = _schema.FirstResultSetTableName;

            return new ReportDefinition(
                actualReportId,
                version,
                $"{_schema.TemplateKeyPrefix}:{actualReportId}",
                sql,
                metadata,
                updatedAt,
                "legacy-sql-server",
                template,
                supplementalQueries);
        }
        catch (LegacyReportDatabaseException)
        {
            throw;
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseTimeout, "Timed out loading the legacy report definition.", exception);
        }
        catch (SqlException exception)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, "Unable to load the legacy report definition.", exception);
        }
    }

    /// <summary>构建定义查询SQL：查询报表ID、FRX模板、SQL、版本、更新时间</summary>
    private string BuildDefinitionQuery()
    {
        var version = VersionExpression() ?? "NULL";
        var updated = string.IsNullOrWhiteSpace(_schema.UpdatedAtColumn) ? "NULL" : _schema.UpdatedAtColumn;
        var nameCondition = string.IsNullOrWhiteSpace(_schema.ReportNameColumn)
            ? string.Empty
            : $" OR {_schema.ReportNameColumn} = @reportId";
        return $"SELECT TOP 1 {_schema.ReportIdColumn}, {_schema.TemplateColumn}, {_schema.SqlColumn}, {version}, {updated} FROM {_schema.DefinitionTable} WHERE {_schema.ReportIdColumn} = @reportId{nameCondition} ORDER BY CASE WHEN {_schema.ReportIdColumn} = @reportId THEN 0 ELSE 1 END";
    }

    /// <summary>
    /// 加载补充查询（子报表SQL）：
    /// 查询以reportId_开头的报表定义（如reportId_Master1、reportId_Master2）。
    /// 这些子报表的SQL会作为额外的数据集注册到FastReport。
    /// </summary>
    private async Task<IReadOnlyList<ReportDataQuery>> LoadSupplementalQueriesAsync(
        SqlConnection connection,
        string reportId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_schema.SupplementalQueryOrderColumn))
            return Array.Empty<ReportDataQuery>();

        await using var command = connection.CreateCommand();
        command.CommandTimeout = TimeoutSeconds();
        command.CommandText = $"""
            SELECT {_schema.ReportIdColumn}, {_schema.SqlColumn}
            FROM {_schema.DefinitionTable}
            WHERE {_schema.ReportIdColumn} LIKE @reportPrefix ESCAPE '\'
              AND {_schema.SqlColumn} IS NOT NULL
            ORDER BY {_schema.SupplementalQueryOrderColumn}, {_schema.ReportIdColumn}
            """;

        // 转义LIKE通配符
        var escapedPrefix = reportId
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "\\_%";
        command.Parameters.Add(new SqlParameter("@reportPrefix", SqlDbType.NVarChar, 260) { Value = escapedPrefix });

        var queries = new List<ReportDataQuery>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var subId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (reader.IsDBNull(1))
                continue;
            var supplementalSql = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(supplementalSql))
                continue;
            queries.Add(new ReportDataQuery(subId, supplementalSql) { SubReportId = subId });
        }
        return queries;
    }

    /// <summary>获取版本列表达式：优先VersionColumn，其次UpdatedAtColumn</summary>
    private string? VersionExpression() => !string.IsNullOrWhiteSpace(_schema.VersionColumn)
        ? _schema.VersionColumn
        : !string.IsNullOrWhiteSpace(_schema.UpdatedAtColumn) ? _schema.UpdatedAtColumn : null;

    /// <summary>获取SQL命令超时（秒），范围1-300</summary>
    private int TimeoutSeconds() => Math.Clamp(_database.CommandTimeoutSeconds, 1, 300);

    /// <summary>验证数据库配置：Provider必须是SqlServer，ConnectionString不能为空</summary>
    private void EnsureConfigured()
    {
        if (!string.Equals(_database.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, $"ReportDatabase provider '{_database.Provider}' is not supported by this SQL Server implementation.");
        if (string.IsNullOrWhiteSpace(_database.ConnectionString))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, "ReportDatabase connection string is not configured.");
    }
}

/// <summary>
/// 旧版数据库模板提供者：解码FRX模板内容。
/// 
/// 支持两种编码方式：
/// 1. Raw：直接存储XML文本
/// 2. Base64Utf8：Base64编码的UTF-8 FRX XML
/// 
/// 自动检测编码方式：
/// - 以<?xml或<Report开头 → 原始XML
/// - 尝试Base64解码 → 如果解码后是有效XML则使用
/// - 包含<Report标签 → 提取从<Report开始的部分
/// </summary>
public sealed class LegacyDatabaseTemplateProvider : ITemplateProvider
{
    public Task<ReportTemplate> GetRequiredAsync(ReportDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(definition.TemplateContent))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.TemplateNotFound, $"Report '{definition.ReportId}' did not include database FRX content.");

        var content = DecodeTemplateContent(definition.TemplateContent, definition.ReportId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return Task.FromResult(new ReportTemplate(definition.TemplateKey, definition.Version, content, hash));
    }

    /// <summary>
    /// 解码模板内容：
    /// 1. 去除BOM和零宽字符
    /// 2. 检测是否为原始XML
    /// 3. 尝试Base64解码
    /// 4. 提取有效XML部分
    /// </summary>
    public static string DecodeTemplateContent(string rawContent, string reportId)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return string.Empty;

        var trimmed = rawContent.Trim().TrimStart('\uFEFF', '\u0000', '\u200B');

        // 1. 如果已经以<?xml或<Report开头，直接返回
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<Report", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // 2. 尝试Base64解码
        try
        {
            var cleanBase64 = trimmed.Replace("\r", "").Replace("\n", "").Replace(" ", "");
            var bytes = Convert.FromBase64String(cleanBase64);
            var decoded = Encoding.UTF8.GetString(bytes).Trim().TrimStart('\uFEFF', '\u0000', '\u200B');
            if (decoded.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                decoded.StartsWith("<Report", StringComparison.OrdinalIgnoreCase) ||
                decoded.Contains("<Report", StringComparison.OrdinalIgnoreCase))
            {
                return decoded;
            }
        }
        catch (FormatException)
        {
            // 不是Base64，继续
        }

        // 3. 从<Report标签开始提取
        var reportIdx = trimmed.IndexOf("<Report", StringComparison.OrdinalIgnoreCase);
        if (reportIdx >= 0)
            return trimmed.Substring(reportIdx);

        // 4. 从<?xml标签开始提取
        var xmlIdx = trimmed.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        if (xmlIdx >= 0)
            return trimmed.Substring(xmlIdx);

        return trimmed;
    }
}

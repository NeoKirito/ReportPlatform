using System.Data;
using System.Text.Json;
using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// 报表数据库错误码枚举。
/// 用于区分不同类型的数据库错误，返回适当的HTTP状态码。
/// </summary>
public enum LegacyReportDatabaseErrorCode
{
    ReportNotFound,           // 报表定义未找到（返回404）
    TemplateNotFound,         // FRX模板内容为空（返回404）
    QueryDefinitionNotFound,  // SQL查询定义为空（返回400）
    DatabaseConnectionFailed, // 数据库连接失败（返回500）
    DatabaseTimeout,          // 数据库查询超时（返回500）
    ParameterBindFailed,      // SQL参数绑定失败（返回400）
    QueryExecutionFailed,     // SQL查询执行失败（返回500）
    DataSetMappingFailed,     // 数据集映射失败（返回500）
    SchemaMappingUnverified   // Schema映射未验证（返回500）
}

/// <summary>
/// 报表数据库异常 - 封装错误码和错误信息。
/// 用于在报表生成流程中抛出带有明确错误码的异常。
/// </summary>
public sealed class LegacyReportDatabaseException(
    LegacyReportDatabaseErrorCode code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public LegacyReportDatabaseErrorCode Code { get; } = code;
}

/// <summary>
/// 报表定义版本信息 - 轻量级缓存令牌。
/// 用于判断缓存是否有效：
/// - 如果配置了版本列（VersionColumn/UpdatedAtColumn），使用数据库中的实际值
/// - 否则使用TTL时间窗口（FallbackTtlSeconds）
/// </summary>
public sealed record ReportDefinitionVersion(
    string CacheToken,        // 缓存键（数据库版本号或TTL桶号）
    bool IsDatabaseVersion,   // 是否来自数据库版本列
    DateTimeOffset? ExpiresAt, // 过期时间（TTL模式）
    string Source);           // 版本来源描述

/// <summary>
/// 报表定义版本提供者接口。
/// 用于获取报表定义的当前版本，判断缓存是否需要刷新。
/// </summary>
public interface IReportDefinitionVersionProvider
{
    Task<ReportDefinitionVersion> GetVersionAsync(ReportRenderRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 报表定义缓存选项。
/// </summary>
public sealed class ReportDefinitionCacheOptions
{
    /// <summary>无版本列时的默认缓存刷新窗口（秒），默认300秒（5分钟）</summary>
    public int FallbackTtlSeconds { get; set; } = 300;
}

/// <summary>
/// 旧版SQL查询参数：包含名称、 DbType 和值。
/// </summary>
public sealed record LegacyQueryParameter(string Name, DbType DbType, object? Value);

/// <summary>
/// 旧版SQL查询绑定：包含替换后的SQL文本和参数列表。
/// </summary>
public sealed record LegacyQueryBinding(string CommandText, IReadOnlyList<LegacyQueryParameter> Parameters);

/// <summary>
/// 旧版SQL参数绑定器接口。
/// 将报表定义中的SQL文本与请求参数绑定，生成可执行的SQL命令。
/// 
/// 支持的参数格式：
/// 1. @name - ADO.NET标准格式
/// 2. [name] - 方括号格式（旧版常用）
/// 3. '${name}' - 单引号美元括号格式（旧版专用）
/// </summary>
public interface ILegacyQueryParameterBinder
{
    LegacyQueryBinding Bind(ReportDefinition definition, ReportRenderRequest request);
}

/// <summary>
/// ADO.NET旧版SQL参数绑定器实现。
/// 
/// 处理流程：
/// 1. 构建参数索引（从request.Parameters和LegacyPayload递归展开）
/// 2. 识别SQL中的参数占位符（@name、[name]、'${name}'）
/// 3. 将占位符替换为@name格式
/// 4. 从参数索引中查找值，转换为ADO.NET参数
/// 5. 处理可选参数（缺失时使用默认值）
/// 
/// 参数别名机制：
/// - djid ↔ grtjgcjjgid ↔ tjdjid ↔ id（体检登记ID）
/// - tjh ↔ tjbh（体检号）
/// - name ↔ nameArr（单复数变体）
/// </summary>
public sealed class AdoNetLegacyQueryParameterBinder : ILegacyQueryParameterBinder
{
    // 正则表达式：匹配 @name 参数（排除已转义的 @@name）
    private static readonly System.Text.RegularExpressions.Regex ParameterPattern = new(
        "(?<!@)@(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // 正则表达式：匹配 [name] 方括号参数
    private static readonly System.Text.RegularExpressions.Regex BracketParameterPattern = new(
        "\\[(?<name>[A-Za-z_][A-Za-z0-9_]*)\\]",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // 正则表达式：匹配 '${name}' 单引号美元括号参数
    private static readonly System.Text.RegularExpressions.Regex QuotedDollarBraceParameterPattern = new(
        "'\\$\\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\\}'",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 绑定SQL参数：将报表定义中的SQL文本与请求参数绑定。
    /// </summary>
    public LegacyQueryBinding Bind(ReportDefinition definition, ReportRenderRequest request)
    {
        if (string.IsNullOrWhiteSpace(definition.SqlText))
            throw new LegacyReportDatabaseException(
                LegacyReportDatabaseErrorCode.QueryDefinitionNotFound,
                $"Report '{definition.ReportId}' has no SQL definition.");

        // 构建参数索引（包含所有可用参数值）
        var source = BuildParameterIndex(request);

        // 尝试解析参数名称（支持Arr后缀变体）
        bool TryResolveParameterName(string name, out string matchedName)
        {
            if (source.ContainsKey(name)) { matchedName = name; return true; }
            if (name.EndsWith("Arr", StringComparison.OrdinalIgnoreCase) && name.Length > 3 && source.ContainsKey(name[..^3])) { matchedName = name[..^3]; return true; }
            if (source.ContainsKey(name + "Arr")) { matchedName = name + "Arr"; return true; }
            matchedName = name;
            return false;
        }

        // 识别SQL中的方括号参数
        var bracketParameterNames = BracketParameterPattern.Matches(definition.SqlText)
            .Select(match => match.Groups["name"].Value)
            .Where(name => TryResolveParameterName(name, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 识别SQL中的单引号美元括号参数
        var dollarBraceParameterNames = QuotedDollarBraceParameterPattern.Matches(definition.SqlText)
            .Select(match => match.Groups["name"].Value)
            .Where(name => TryResolveParameterName(name, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 将 '${name}' 替换为 @name
        var commandText = QuotedDollarBraceParameterPattern.Replace(definition.SqlText, match =>
        {
            var name = match.Groups["name"].Value;
            return TryResolveParameterName(name, out var resolved) ? "@" + resolved : match.Value;
        });

        // 将 [name] 替换为 @name
        commandText = BracketParameterPattern.Replace(commandText, match =>
        {
            var name = match.Groups["name"].Value;
            return TryResolveParameterName(name, out var resolved) ? "@" + resolved : match.Value;
        });

        // 提取所有 @name 参数
        var parameters = new List<LegacyQueryParameter>();
        foreach (System.Text.RegularExpressions.Match match in ParameterPattern.Matches(commandText))
        {
            var name = match.Groups["name"].Value;
            if (parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;  // 避免重复参数

            if (!source.TryGetValue(name, out var value))
            {
                // 尝试Arr后缀变体
                if (name.EndsWith("Arr", StringComparison.OrdinalIgnoreCase) && source.TryGetValue(name[..^3], out var singular))
                    value = singular;
                else if (source.TryGetValue(name + "Arr", out var arrVal))
                    value = arrVal;
                else if (IsOptionalParameter(name))
                    value = default;  // 可选参数使用默认值
                else
                    throw new LegacyReportDatabaseException(
                        LegacyReportDatabaseErrorCode.ParameterBindFailed,
                        $"Report '{definition.ReportId}' SQL requires parameter '@{name}', but legacy payload does not contain it.");
            }

            parameters.Add(ToParameter(
                name,
                value,
                bracketParameterNames.Contains(name) || dollarBraceParameterNames.Contains(name)));
        }

        return new LegacyQueryBinding(commandText, parameters);
    }

    /// <summary>
    /// 判断是否为可选参数（缺失时使用空字符串默认值）。
    /// 这些参数在旧版报表中经常缺失。
    /// </summary>
    private static bool IsOptionalParameter(string name) =>
        string.Equals(name, "yhmc", StringComparison.OrdinalIgnoreCase) ||      // 用户名称
        string.Equals(name, "servertime", StringComparison.OrdinalIgnoreCase) || // 服务器时间
        string.Equals(name, "czy", StringComparison.OrdinalIgnoreCase) ||        // 操作员
        string.Equals(name, "hospitalName", StringComparison.OrdinalIgnoreCase) || // 医院名称
        string.Equals(name, "newTime", StringComparison.OrdinalIgnoreCase);      // 新时间

    /// <summary>
    /// 构建参数索引：
    /// 1. 从request.Parameters提取显式参数
    /// 2. 从request.LegacyPayload递归展开嵌套对象
    /// 3. 生成单复数变体（name ↔ nameArr）
    /// 4. 添加医院名称别名（djid ↔ grtjgcjjgid ↔ tjdjid ↔ id）
    /// </summary>
    private static Dictionary<string, JsonElement> BuildParameterIndex(ReportRenderRequest request)
    {
        var values = new Dictionary<string, JsonElement>(request.Parameters, StringComparer.OrdinalIgnoreCase);
        if (request.LegacyPayload is { ValueKind: JsonValueKind.Object } payload)
            IndexPayloadValues(payload, values);

        NormalizePluralSingularVariants(values);
        AddHospitalAliases(values);
        NormalizePluralSingularVariants(values);  // 再次处理别名生成的变体
        return values;
    }

    /// <summary>
    /// 生成单复数变体：
    /// - 如果有 nameArr，则添加 name（取第一个元素）
    /// - 如果有 name，则添加 nameArr
    /// </summary>
    private static void NormalizePluralSingularVariants(Dictionary<string, JsonElement> values)
    {
        foreach (var (key, val) in values.ToArray())
        {
            if (key.EndsWith("Arr", StringComparison.OrdinalIgnoreCase) && key.Length > 3)
            {
                var singular = key[..^3];
                if (!values.ContainsKey(singular))
                    values[singular] = val.Clone();
            }
            else
            {
                var plural = key + "Arr";
                if (!values.ContainsKey(plural))
                    values[plural] = val.Clone();
            }
        }
    }

    /// <summary>
    /// 添加医院名称别名：
    /// - djid ↔ grtjgcjjgid ↔ tjdjid ↔ id（体检登记ID）
    /// - tjh ↔ tjbh（体检号）
    /// 
    /// 不同版本的报表可能使用不同的参数名引用同一个值。
    /// </summary>
    private static void AddHospitalAliases(Dictionary<string, JsonElement> values)
    {
        // 体检登记ID别名
        string[] idKeys = ["djid", "grtjgcjjgid", "tjdjid", "id"];
        JsonElement? foundIdVal = null;
        foreach (var key in idKeys)
        {
            if (values.TryGetValue(key, out var val) && val.ValueKind != JsonValueKind.Null && val.ValueKind != JsonValueKind.Undefined)
            {
                foundIdVal = val;
                break;
            }
        }
        if (foundIdVal.HasValue)
        {
            foreach (var key in idKeys)
            {
                if (!values.ContainsKey(key))
                    values[key] = foundIdVal.Value.Clone();
            }
        }

        // 体检号别名
        if (values.TryGetValue("tjh", out var tjhVal) && !values.ContainsKey("tjbh"))
            values["tjbh"] = tjhVal.Clone();
        else if (values.TryGetValue("tjbh", out var tjbhVal) && !values.ContainsKey("tjh"))
            values["tjh"] = tjbhVal.Clone();
    }

    /// <summary>
    /// 从JSON对象中递归提取参数值：
    /// - 标量类型（字符串、数字、布尔、null）：直接添加
    /// - 数组类型：取第一个元素
    /// - 对象类型：递归展开
    /// 
    /// 同时生成单复数变体。
    /// </summary>
    private static void IndexPayloadValues(JsonElement payload, Dictionary<string, JsonElement> values)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
            {
                values[property.Name] = property.Value.Clone();
                // 生成单复数变体
                if (property.Name.EndsWith("Arr", StringComparison.OrdinalIgnoreCase))
                {
                    var singular = property.Name[..^3];
                    if (!values.ContainsKey(singular))
                        values[singular] = property.Value.Clone();
                }
                else
                {
                    var plural = property.Name + "Arr";
                    if (!values.ContainsKey(plural))
                        values[plural] = property.Value.Clone();
                }
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var length = property.Value.GetArrayLength();
                if (length > 0)
                {
                    var first = property.Value[0];
                    values[property.Name] = first.Clone();
                    // 生成单复数变体
                    if (property.Name.EndsWith("Arr", StringComparison.OrdinalIgnoreCase))
                    {
                        var singular = property.Name[..^3];
                        if (!values.ContainsKey(singular))
                            values[singular] = first.Clone();
                    }
                    else
                    {
                        var plural = property.Name + "Arr";
                        if (!values.ContainsKey(plural))
                            values[plural] = first.Clone();
                    }
                }
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
                IndexPayloadValues(property.Value, values);
        }
    }

    /// <summary>
    /// JSON元素转ADO.NET参数：
    /// - String → DbType.String（或AnsiString）
    /// - Int64 → DbType.Int64
    /// - Decimal → DbType.Decimal
    /// - Boolean → DbType.Boolean
    /// - Null → DbType.Object (DBNull)
    /// - Array → 取第一个元素递归转换
    /// - Undefined → DBNull（可选参数返回空字符串）
    /// </summary>
    private static LegacyQueryParameter ToParameter(string name, JsonElement value, bool useAnsiString)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            return new LegacyQueryParameter(name, useAnsiString ? DbType.AnsiString : DbType.String, IsOptionalParameter(name) ? string.Empty : DBNull.Value);

        return value.ValueKind switch
        {
            JsonValueKind.String => new LegacyQueryParameter(name, useAnsiString ? DbType.AnsiString : DbType.String, value.GetString()),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => new LegacyQueryParameter(name, DbType.Int64, integer),
            JsonValueKind.Number when value.TryGetDecimal(out var number) => new LegacyQueryParameter(name, DbType.Decimal, number),
            JsonValueKind.True => new LegacyQueryParameter(name, DbType.Boolean, true),
            JsonValueKind.False => new LegacyQueryParameter(name, DbType.Boolean, false),
            JsonValueKind.Null => new LegacyQueryParameter(name, DbType.Object, DBNull.Value),
            JsonValueKind.Array when value.GetArrayLength() > 0 => ToParameter(name, value[0], useAnsiString),
            _ => new LegacyQueryParameter(name, useAnsiString ? DbType.AnsiString : DbType.String, IsOptionalParameter(name) ? string.Empty : DBNull.Value)
        };
    }
}

using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// 默认报表水印解析器实现。
/// 支持现场通过 config.ini / appsettings.json 进行灵活的规则控制：
/// 1. 报表排除（如条码、导引单永不打水印）；
/// 2. 请求级覆盖（调用方传参优先）；
/// 3. 条件隐藏（如审核状态为已审核时隐藏水印）；
/// 4. 多候选字段提取（解决不同报表列名不同的问题，如 xm,hzxm,b_name）；
/// 5. 报表专属字段覆盖（WatermarkField_报表ID）；
/// 6. 固定文本与宏模板组装（{Field}、{HospitalName}）；
/// 7. 机构名称安全回退（从维护库只读获取）。
/// </summary>
public sealed class DefaultWatermarkResolver : IWatermarkResolver
{
    private readonly IOptions<WatermarkPolicyOptions> _options;
    private readonly IWatermarkTextProvider _watermarkTextProvider;
    private readonly ILogger<DefaultWatermarkResolver> _logger;

    public DefaultWatermarkResolver(
        IOptions<WatermarkPolicyOptions> options,
        IWatermarkTextProvider watermarkTextProvider,
        ILogger<DefaultWatermarkResolver>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _watermarkTextProvider = watermarkTextProvider ?? throw new ArgumentNullException(nameof(watermarkTextProvider));
        _logger = logger ?? NullLogger<DefaultWatermarkResolver>.Instance;
    }

    /// <inheritdoc />
    public async Task<WatermarkOptions> ResolveAsync(
        ReportDefinition definition,
        ReportRenderRequest request,
        ReportDataSet reportData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reportData);
        cancellationToken.ThrowIfCancellationRequested();

        var config = _options.Value ?? new WatermarkPolicyOptions();

        // 1. 检查排除报表列表（例如项目条码 xmtm、体检指引单 tjdj 严禁打水印）
        if (IsReportExcluded(definition.ReportId, config.ExcludeReports))
        {
            _logger.LogDebug("Report '{ReportId}' matches watermark exclude list; skipping watermark.", definition.ReportId);
            return new WatermarkOptions(Enabled: false);
        }

        // 2. 检查请求显式配置：若上游调用方明确要求禁用水印，直接跳过
        if (request.Watermark is { Enabled: false })
        {
            return request.Watermark;
        }

        // 3. 检查开关状态：若请求未显式开启，且配置文件总开关未开启，则默认不打水印
        var isExplicitlyEnabledInRequest = request.Watermark is { Enabled: true };
        if (!config.Enabled && !isExplicitlyEnabledInRequest)
        {
            return new WatermarkOptions(Enabled: false);
        }

        // 4. 检查条件隐藏规则（例如已审核报告隐藏水印：ConditionField="sh_flag", HideWhenValue="1"）
        if (!string.IsNullOrWhiteSpace(config.ConditionField) && !string.IsNullOrWhiteSpace(config.HideWhenValue))
        {
            var conditionVal = ExtractValue(reportData, request, config.ConditionField);
            if (string.Equals(conditionVal, config.HideWhenValue.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Report '{ReportId}' condition matched '{Field}'='{Value}'; skipping watermark.",
                    definition.ReportId, config.ConditionField, conditionVal);
                return new WatermarkOptions(Enabled: false);
            }
        }

        // 5. 提取并组装水印文字内容
        string? text = null;

        // 5.1 优先级 1：调用方在请求中显式指定的文字
        if (isExplicitlyEnabledInRequest && !string.IsNullOrWhiteSpace(request.Watermark?.Text))
        {
            text = request.Watermark.Text.Trim();
        }
        // 5.2 优先级 2：配置文件中指定的全局固定文字
        else if (!string.IsNullOrWhiteSpace(config.Text))
        {
            text = config.Text.Trim();
        }
        // 5.3 优先级 3：动态字段提取（解决不同报表字段名称不同的问题）
        else
        {
            // 检查当前特定报表是否有专属字段配置（如 WatermarkField_jktjbbd=xm）
            string? candidateFields = null;
            if (config.ReportFields.TryGetValue(definition.ReportId, out var repField) && !string.IsNullOrWhiteSpace(repField))
            {
                candidateFields = repField;
            }
            else
            {
                candidateFields = config.Field;
            }

            // 在报表数据及入参中按候选顺序查找第一个非空字段值
            string? extractedVal = null;
            if (!string.IsNullOrWhiteSpace(candidateFields))
            {
                extractedVal = ExtractValue(reportData, request, candidateFields);
            }

            // 是否需要获取数据库维护库机构名称（若模板中包含占位符，或字段未取到作为回退）
            string? hospitalName = null;
            var needsHospitalName = config.Template?.Contains("{HospitalName}", StringComparison.OrdinalIgnoreCase) == true
                || string.IsNullOrWhiteSpace(extractedVal);

            if (needsHospitalName)
            {
                hospitalName = await _watermarkTextProvider.GetWatermarkTextAsync(cancellationToken).ConfigureAwait(false);
            }

            // 如果配置了模板，进行宏替换
            if (!string.IsNullOrWhiteSpace(config.Template))
            {
                text = config.Template
                    .Replace("{Field}", extractedVal ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("{HospitalName}", hospitalName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim();
            }
            else
            {
                // 未配置模板：优先取字段值，若字段未取到则回退至医院机构名称
                text = !string.IsNullOrWhiteSpace(extractedVal) ? extractedVal : hospitalName;
            }
        }

        // 如果最终未获取到任何有效文字，跳过水印
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("No watermark text resolved for report '{ReportId}'; skipping watermark.", definition.ReportId);
            return new WatermarkOptions(Enabled: false);
        }

        // 6. 综合样式参数（请求参数优先，其次为配置项，最后为系统安全默认值）
        var opacity = request.Watermark is { Opacity: > 0 } ? request.Watermark.Opacity : (config.Opacity > 0 ? config.Opacity : 0.12);
        var angle = request.Watermark is { Angle: not 0 } ? request.Watermark.Angle : (config.Angle != 0 ? config.Angle : -30);
        var fontSize = request.Watermark is { FontSize: > 0 } ? request.Watermark.FontSize : (config.FontSize > 0 ? config.FontSize : 54);

        return new WatermarkOptions(
            Enabled: true,
            Text: text.Trim(),
            Opacity: Math.Clamp(opacity, 0.01, 1.0),
            Angle: angle,
            FontSize: Math.Clamp(fontSize, 10f, 150f));
    }

    /// <summary>
    /// 判断当前报表 ID 是否在排除列表中。
    /// </summary>
    private static bool IsReportExcluded(string reportId, string? excludeList)
    {
        if (string.IsNullOrWhiteSpace(excludeList) || string.IsNullOrWhiteSpace(reportId))
            return false;

        var tokens = excludeList.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(t => string.Equals(t, reportId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 在报表数据集与请求参数中，依次按候选列名检索第一个有效非空值。
    /// </summary>
    private static string? ExtractValue(ReportDataSet reportData, ReportRenderRequest request, string candidateFields)
    {
        if (string.IsNullOrWhiteSpace(candidateFields))
            return null;

        var candidates = candidateFields.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (candidates.Length == 0)
            return null;

        // 1. 排序报表表列表：优先主表 Master / Master1，其次是其它从表
        var prioritizedTables = new List<DataTable>();
        if (reportData.Tables.TryGetValue("Master", out var masterTable))
            prioritizedTables.Add(masterTable);
        if (reportData.Tables.TryGetValue("Master1", out var master1Table) && master1Table != masterTable)
            prioritizedTables.Add(master1Table);

        foreach (var kvp in reportData.Tables)
        {
            if (!prioritizedTables.Contains(kvp.Value))
                prioritizedTables.Add(kvp.Value);
        }

        // 2. 依次按候选字段查找
        foreach (var candidate in candidates)
        {
            // 2.1 先在已执行的 SQL 结果数据表中查找
            foreach (var table in prioritizedTables)
            {
                if (table.Rows.Count == 0)
                    continue;

                if (table.Columns.Contains(candidate))
                {
                    var val = table.Rows[0][candidate];
                    if (val is not null && val != DBNull.Value)
                    {
                        var str = Convert.ToString(val, CultureInfo.InvariantCulture)?.Trim();
                        if (!string.IsNullOrWhiteSpace(str))
                            return str;
                    }
                }
            }

            // 2.2 若数据表中未找到，尝试从请求入参中提取（如上游传入的 xm、djid 等）
            if (request.Parameters.TryGetValue(candidate, out var jsonElement))
            {
                var paramVal = JsonScalarToString(jsonElement)?.Trim();
                if (!string.IsNullOrWhiteSpace(paramVal))
                    return paramVal;
            }
        }

        return null;
    }

    /// <summary>
    /// 将 JSON 标量元素安全转换为字符串。
    /// </summary>
    private static string? JsonScalarToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        _ => null
    };
}

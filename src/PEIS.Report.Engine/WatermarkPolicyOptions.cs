namespace PEIS.Report.Engine;

/// <summary>
/// 报表水印策略配置选项。
/// 现场可通过 config.ini 或 appsettings.json 进行灵活配置，满足开/关、动态取不同报表字段、
/// 固定文字、条码/单据排除以及条件隐藏等所有现场需求。
/// </summary>
public sealed class WatermarkPolicyOptions
{
    /// <summary>
    /// 水印总开关。
    /// true: 开启水印；false: 关闭水印（默认关闭，保持纸质报告清洁）。
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 动态字段提取配置（用于解决不同报表列名不一致的问题）。
    /// 支持配置逗号或分号分隔的多个候选列名，例如："xm,hzxm,b_name,name,patient_name,dwmc"。
    /// 渲染引擎会在当前报表的查询数据集中依次查找存在的列，取第一项非空值作为水印文字。
    /// </summary>
    public string? Field { get; set; }

    /// <summary>
    /// 固定水印文字（优先级高于动态字段提取）。
    /// 示例："仅供预览，不得作为正式报告" 或 "内部草稿"。
    /// 若配置了此项，所有启用水印的报表均统一显示该文字。
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// 水印文本组装模板。
    /// 支持宏占位符：
    /// - {Field}: 替换为从当前报表数据中提取到的字段值（如患者姓名）
    /// - {HospitalName}: 替换为数据库维护库 dbo.qx_hospital.jgmc 的机构名称
    /// 示例："{Field} - 仅供核对" 或 "{Field} {HospitalName}"。
    /// 若为空，则直接使用提取到的字段值或机构名称。
    /// </summary>
    public string? Template { get; set; }

    /// <summary>
    /// 强制排除（不打水印）的报表 ID 列表，使用逗号分隔。
    /// 默认排除条码单 xmtm 和体检指引单 tjdj，避免干扰条形码扫码或密集排版。
    /// </summary>
    public string? ExcludeReports { get; set; } = "xmtm,tjdj";

    /// <summary>
    /// 条件隐藏字段名（例如 "sh_flag" 审核标志、"status" 状态）。
    /// 当报表数据中该字段的值等于 <see cref="HideWhenValue"/> 时，自动不显示水印。
    /// </summary>
    public string? ConditionField { get; set; }

    /// <summary>
    /// 触发隐藏水印的特定值（例如 "1" 代表已审核，已审核报告不显示水印）。
    /// </summary>
    public string? HideWhenValue { get; set; }

    /// <summary>
    /// 水印文字透明度（0.0 ~ 1.0），默认 0.12（浅灰色半透明，不遮挡正文）。
    /// </summary>
    public double Opacity { get; set; } = 0.12;

    /// <summary>
    /// 水印旋转倾斜角度（角度制），默认 -30 度（对角线向上倾斜）。
    /// </summary>
    public double Angle { get; set; } = -30;

    /// <summary>
    /// 水印文字字号大小，默认 54pt。
    /// </summary>
    public float FontSize { get; set; } = 54;

    /// <summary>
    /// 特定报表的专属字段覆盖字典。
    /// key 为报表 ID（不区分大小写），value 为该报表专门提取的列名（支持多候选）。
    /// 例如：{ "jktjbbd": "xm", "zytj": "worker_name" }。
    /// 现场可在 config.ini 中使用形如 WatermarkField_jktjbbd=xm 进行配置。
    /// </summary>
    public Dictionary<string, string> ReportFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

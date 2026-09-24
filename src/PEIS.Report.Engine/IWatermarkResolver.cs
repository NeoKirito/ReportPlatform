using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// 报表水印解析器接口。
/// 负责根据请求参数、报表定义、现场配置以及实际执行 SQL 返回的报表数据，
/// 动态综合裁定当前报表是否需要水印、提取哪个字段作为水印文本、应用何种视觉样式。
/// </summary>
public interface IWatermarkResolver
{
    /// <summary>
    /// 解析最终应用于报表的水印选项。
    /// </summary>
    /// <param name="definition">当前报表定义（包含报表 ID、报表名称等元数据）</param>
    /// <param name="request">当前渲染请求（包含入参参数与上游显式指定的水印选项）</param>
    /// <param name="reportData">当前报表执行 SQL 查询得到的实际数据结果集</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>最终的水印选项（若判定不显示水印，返回 Enabled=false）</returns>
    Task<WatermarkOptions> ResolveAsync(
        ReportDefinition definition,
        ReportRenderRequest request,
        ReportDataSet reportData,
        CancellationToken cancellationToken);
}

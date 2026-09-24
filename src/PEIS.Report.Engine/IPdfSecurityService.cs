namespace PEIS.Report.Engine;

/// <summary>
/// PDF 安全与防篡改服务接口。
/// 对渲染完成的原始 PDF 字节流进行安全加固，配置权限控制以防止使用 PDF 编辑工具篡改报告内容。
/// </summary>
public interface IPdfSecurityService
{
    /// <summary>
    /// 对 PDF 字节流应用安全策略。
    /// 若策略未开启或处理失败，将安全回退返回原始 PDF 字节流，确保不影响核心医疗业务。
    /// </summary>
    /// <param name="pdfBytes">原始 PDF 字节流</param>
    /// <param name="overrideOptions">可选的覆盖配置（为空则使用全局策略）</param>
    /// <returns>应用权限加密后的 PDF 字节流</returns>
    byte[] ApplySecurity(byte[] pdfBytes, PdfSecurityOptions? overrideOptions = null);
}

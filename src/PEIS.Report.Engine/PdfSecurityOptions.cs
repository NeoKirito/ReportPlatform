namespace PEIS.Report.Engine;

/// <summary>
/// PDF 安全与防篡改策略配置。
/// 支持通过 config.ini 和 appsettings.json 进行现场配置。
/// 通过设置所有者权限密码（Owner Password），在保证普通用户/受检者直接查阅与打印的同时，
/// 阻止 Adobe Acrobat、福昕等专业 PDF 编辑软件修改正文内容。
/// </summary>
public sealed class PdfSecurityOptions
{
    /// <summary>
    /// 是否启用 PDF 权限保护。默认 false。
    /// 启用后，导出的 PDF 会被设置权限控制，禁止未经授权的编辑与页面改动。
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 权限所有者密码（Owner Password）。
    /// 若为空或未配置，系统会自动生成强随机密码，使外部人员无法猜解。
    /// </summary>
    public string? OwnerPassword { get; set; }

    /// <summary>
    /// 用户打开密码（User Password）。
    /// 医疗体检报告通常应保持为空，以便受检者和医护人员直接双击打开浏览，无需输入密码。
    /// </summary>
    public string? UserPassword { get; set; } = string.Empty;

    /// <summary>
    /// 是否允许打印文档。体检报告通常必须允许打印，默认 true。
    /// </summary>
    public bool AllowPrint { get; set; } = true;

    /// <summary>
    /// 是否允许复制正文和图片内容。默认 false（防止文字复制与内容盗取）。
    /// </summary>
    public bool AllowCopy { get; set; } = false;

    /// <summary>
    /// 是否允许修改文档内容（禁止正文编辑与页面增删）。默认 false。
    /// </summary>
    public bool AllowModify { get; set; } = false;

    /// <summary>
    /// 是否允许添加批注与填写表单。默认 false。
    /// </summary>
    public bool AllowAnnotate { get; set; } = false;
}

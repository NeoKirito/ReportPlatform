using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdfSharp.Pdf.IO;

namespace PEIS.Report.Engine;

/// <summary>
/// 默认 PDF 安全服务实现（基于 PDFsharp）。
/// 为 PDF 设置权限密码（Owner Password），在保证普通用户/浏览器直接打开浏览与打印的同时，
/// 阻止第三方 PDF 编辑器（如 Adobe Acrobat、Foxit 等）修改正文或提取内容。
/// </summary>
public sealed class PdfSecurityService : IPdfSecurityService
{
    private readonly IOptions<PdfSecurityOptions> _options;
    private readonly ILogger<PdfSecurityService> _logger;

    public PdfSecurityService(
        IOptions<PdfSecurityOptions> options,
        ILogger<PdfSecurityService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<PdfSecurityService>.Instance;
    }

    /// <inheritdoc />
    public byte[] ApplySecurity(byte[] pdfBytes, PdfSecurityOptions? overrideOptions = null)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
            return pdfBytes ?? Array.Empty<byte>();

        var options = overrideOptions ?? _options.Value ?? new PdfSecurityOptions();
        if (!options.Enabled)
        {
            return pdfBytes;
        }

        try
        {
            using var inStream = new MemoryStream(pdfBytes);
            using var document = PdfReader.Open(inStream, PdfDocumentOpenMode.Modify);

            // 若现场未指定密码，自动生成高强度随机 32 位十六进制密码，使外界无法猜解
            var ownerPassword = string.IsNullOrWhiteSpace(options.OwnerPassword)
                ? GenerateSecureRandomPassword()
                : options.OwnerPassword.Trim();

            document.SecuritySettings.OwnerPassword = ownerPassword;
            document.SecuritySettings.UserPassword = options.UserPassword ?? string.Empty;

            // 1. 禁止修改正文内容与页面结构（防止修改体检结论和化验指标）
            document.SecuritySettings.PermitModifyDocument = options.AllowModify;
            document.SecuritySettings.PermitAssembleDocument = options.AllowModify;

            // 2. 打印控制（体检报告通常必须允许打印）
            document.SecuritySettings.PermitPrint = options.AllowPrint;

            // 3. 内容提取控制（是否允许复制文本/图像）
            document.SecuritySettings.PermitExtractContent = options.AllowCopy;

            // 4. 批注与表单控制
            document.SecuritySettings.PermitAnnotations = options.AllowAnnotate;
            document.SecuritySettings.PermitFormsFill = options.AllowAnnotate;

            using var outStream = new MemoryStream();
            document.Save(outStream);
            _logger.LogDebug("PDF security successfully applied with owner-password protection.");
            return outStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply PDF security encryption; fallback to returning original unencrypted PDF.");
            return pdfBytes;
        }
    }

    private static string GenerateSecureRandomPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes);
    }
}

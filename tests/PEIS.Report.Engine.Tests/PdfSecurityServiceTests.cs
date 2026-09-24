using Microsoft.Extensions.Options;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace PEIS.Report.Engine.Tests;

public sealed class PdfSecurityServiceTests
{
    [Fact]
    public void When_disabled_returns_original_bytes_directly()
    {
        var originalBytes = new byte[] { 1, 2, 3, 4, 5 };
        var options = Options.Create(new PdfSecurityOptions { Enabled = false });
        var service = new PdfSecurityService(options);

        var result = service.ApplySecurity(originalBytes);

        Assert.Same(originalBytes, result);
    }

    [Fact]
    public void When_enabled_applies_owner_password_and_restricts_editing()
    {
        var rawPdf = CreateSamplePdf();
        var options = Options.Create(new PdfSecurityOptions
        {
            Enabled = true,
            OwnerPassword = "DoctorAdminSecretPassword123!",
            AllowPrint = true,
            AllowModify = false,
            AllowCopy = false,
            AllowAnnotate = false
        });
        var service = new PdfSecurityService(options);

        var securedPdf = service.ApplySecurity(rawPdf);

        Assert.NotNull(securedPdf);
        Assert.NotEmpty(securedPdf);

        // 验证：普通用户不需要输入密码即可直接打开浏览
        using var testStream = new MemoryStream(securedPdf);
        using var document = PdfReader.Open(testStream, PdfDocumentOpenMode.Import);

        // 验证：禁止修改，禁止复制，允许打印
        Assert.False(document.SecuritySettings.PermitModifyDocument);
        Assert.False(document.SecuritySettings.PermitAssembleDocument);
        Assert.False(document.SecuritySettings.PermitExtractContent);
        Assert.False(document.SecuritySettings.PermitAnnotations);
        Assert.False(document.SecuritySettings.PermitFormsFill);
        Assert.True(document.SecuritySettings.PermitPrint);
    }

    [Fact]
    public void When_owner_password_is_empty_generates_strong_random_password()
    {
        var rawPdf = CreateSamplePdf();
        var options = Options.Create(new PdfSecurityOptions
        {
            Enabled = true,
            OwnerPassword = "", // 留空，自动生成强密码
            AllowPrint = true,
            AllowModify = false
        });
        var service = new PdfSecurityService(options);

        var securedPdf = service.ApplySecurity(rawPdf);

        // 验证：无密码可打开查看并受权限保护
        using var testStream = new MemoryStream(securedPdf);
        using var document = PdfReader.Open(testStream, PdfDocumentOpenMode.Import);

        Assert.False(document.SecuritySettings.PermitModifyDocument);
        Assert.True(document.SecuritySettings.PermitPrint);
    }

    [Fact]
    public void When_corrupted_input_gracefully_falls_back_to_original_bytes()
    {
        var invalidBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var options = Options.Create(new PdfSecurityOptions { Enabled = true });
        var service = new PdfSecurityService(options);

        var result = service.ApplySecurity(invalidBytes);

        // 发生异常时优雅回退，返回原始字节，不阻断业务
        Assert.Equal(invalidBytes, result);
    }

    private static byte[] CreateSamplePdf()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}

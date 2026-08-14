using System.Diagnostics;
using System.Text;
using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// Development-only renderer. Replace with FastReportReportRenderer when the licensed
/// FastReport package/reference and the production FRX/database schema are wired in.
/// </summary>
public sealed class StubReportRenderer : IReportRenderer
{
    public Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var pdf = MinimalPdf($"PEIS Report Platform\nReportId: {request.ReportId}\nStub renderer - replace with FastReport adapter.");
        sw.Stop();

        return Task.FromResult(new ReportRenderResult(
            pdf,
            request.FileName ?? $"{request.ReportId}.pdf",
            1,
            [new ReportStageTiming("StubRender", sw.ElapsedMilliseconds)]));
    }

    private static byte[] MinimalPdf(string text)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "").Replace("\n", ") Tj 0 -18 Td (");
        var stream = $"BT /F1 12 Tf 72 760 Td ({Escape(text)}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.4");
        writer.Flush();
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(ms.Position);
            writer.WriteLine($"{i + 1} 0 obj");
            writer.WriteLine(objects[i]);
            writer.WriteLine("endobj");
            writer.Flush();
        }

        var xref = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objects.Length + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var offset in offsets.Skip(1)) writer.WriteLine($"{offset:0000000000} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {objects.Length + 1} /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xref);
        writer.WriteLine("%%EOF");
        writer.Flush();
        return ms.ToArray();
    }
}

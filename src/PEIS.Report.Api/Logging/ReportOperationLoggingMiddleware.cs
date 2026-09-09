using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PEIS.Report.Api.Logging;

public sealed class ReportOperationLoggingMiddleware(
    RequestDelegate next,
    ILogger<ReportOperationLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Skip static files and health checks from spamming the operation log
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var clientIp = GetClientIp(context);
        var method = context.Request.Method;
        var sw = Stopwatch.StartNew();

        string? reportKey = null;
        string? targetKey = null;

        // 1. Try extracting from query string
        if (context.Request.Query.TryGetValue("templateid", out var qTemplateId)) reportKey = qTemplateId.ToString();
        if (context.Request.Query.TryGetValue("bbid", out var qBbid)) reportKey = qBbid.ToString();
        if (context.Request.Query.TryGetValue("dwtjgcjjgidArr", out var qDwtj)) targetKey = $"单位:{qDwtj}";
        else if (context.Request.Query.TryGetValue("grtjgcjjgidArr", out var qGrtj)) targetKey = $"个人:{qGrtj}";

        // 2. If POST and body might have JSON, peek the body if buffering is enabled
        if (context.Request.ContentLength > 0 && context.Request.ContentType != null &&
            context.Request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.EnableBuffering();
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;

                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (string.IsNullOrEmpty(reportKey))
                    {
                        if (root.TryGetProperty("bbid", out var bbid)) reportKey = bbid.GetString();
                        else if (root.TryGetProperty("templateid", out var tid)) reportKey = tid.GetString();
                        else if (root.TryGetProperty("fileName", out var fn)) reportKey = fn.GetString();
                    }
                    if (string.IsNullOrEmpty(targetKey) && root.TryGetProperty("djh", out var djh))
                    {
                        if (djh.TryGetProperty("dwtjgcjjgid", out var dw) && !string.IsNullOrEmpty(dw.GetString())) targetKey = $"单位:{dw.GetString()}";
                        else if (djh.TryGetProperty("grtjgcjjgid", out var gr) && !string.IsNullOrEmpty(gr.GetString())) targetKey = $"个人:{gr.GetString()}";
                    }
                }
            }
            catch
            {
                // Reset position if parse failed
                context.Request.Body.Position = 0;
            }
        }

        var reportSummary = $"[报表: {reportKey ?? "通用/交付"}] [对象: {targetKey ?? "未指定"}]";

        try
        {
            await next(context);
            sw.Stop();

            var statusCode = context.Response.StatusCode;
            var bytes = context.Response.ContentLength ?? 0;
            var sizeStr = bytes > 0 ? $" ({bytes / 1024.0:F1} KB)" : "";

            if (statusCode >= 200 && statusCode < 300)
            {
                logger.LogInformation(
                    "[业务审计] ✓ [成功] 来自IP: {ClientIp} | 接口: {Method} {Path} | {Summary} | 状态: {StatusCode} | 耗时: {ElapsedMs}ms{SizeStr}",
                    clientIp, method, path, reportSummary, statusCode, sw.ElapsedMilliseconds, sizeStr);
            }
            else
            {
                logger.LogWarning(
                    "[业务审计] ⚠ [异常] 来自IP: {ClientIp} | 接口: {Method} {Path} | {Summary} | 状态: {StatusCode} | 耗时: {ElapsedMs}ms",
                    clientIp, method, path, reportSummary, statusCode, sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex,
                "[业务审计] ✗ [崩溃] 来自IP: {ClientIp} | 接口: {Method} {Path} | {Summary} | 耗时: {ElapsedMs}ms | 错误: {Message}",
                clientIp, method, path, reportSummary, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    private static string GetClientIp(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) && !string.IsNullOrWhiteSpace(forwarded))
        {
            var ip = forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrWhiteSpace(ip)) return ip;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    }
}

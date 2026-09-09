using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PEIS.Report.Contracts.Logging;

/// <summary>
/// Background worker that periodically scans the log directory, archives logs older than 30 days
/// into monthly zip packages (e.g. logs/archive/{app}-yyyy-MM.zip), and removes the uncompressed files.
/// </summary>
public sealed class LogArchiveWorker(
    RollingFileLoggerOptions options,
    ILogger<LogArchiveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to avoid slowing down immediate startup
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunArchiveCycle();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[日志归档] 自动归档过程遇到异常: {Message}", ex.Message);
            }

            // Check once every 24 hours
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
        }
    }

    public void RunArchiveCycle()
    {
        var logDir = Path.IsPathRooted(options.LogDirectory)
            ? options.LogDirectory
            : Path.Combine(AppContext.BaseDirectory, options.LogDirectory);

        if (!Directory.Exists(logDir)) return;

        var archiveDir = Path.Combine(logDir, "archive");
        if (!Directory.Exists(archiveDir))
        {
            Directory.CreateDirectory(archiveDir);
        }

        var prefix = options.FilePrefix;
        var thresholdDate = DateTime.Today.AddDays(-Math.Max(1, options.RetentionDays));
        var logFiles = Directory.GetFiles(logDir, $"{prefix}-*.log", SearchOption.TopDirectoryOnly);

        foreach (var file in logFiles)
        {
            var fileName = Path.GetFileName(file);
            // Expected format: {prefix}-yyyy-MM-dd.log
            var rawDate = Path.GetFileNameWithoutExtension(fileName);
            if (rawDate.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                rawDate = rawDate[(prefix.Length + 1)..];
            }

            DateTime fileDate;
            if (DateTime.TryParseExact(rawDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                fileDate = parsedDate;
            }
            else
            {
                fileDate = File.GetLastWriteTime(file).Date;
            }

            // If older than retention days (default 30 days)
            if (fileDate < thresholdDate)
            {
                var monthKey = fileDate.ToString("yyyy-MM");
                var zipPath = Path.Combine(archiveDir, $"{prefix}-{monthKey}.zip");

                try
                {
                    using (var zipArchive = ZipFile.Open(zipPath, ZipArchiveMode.Update))
                    {
                        // Remove existing entry with same name if any
                        var existing = zipArchive.GetEntry(fileName);
                        existing?.Delete();

                        zipArchive.CreateEntryFromFile(file, fileName, CompressionLevel.Optimal);
                    }

                    // Delete original log file after successful archive
                    File.Delete(file);
                    logger.LogInformation("[日志归档] 成功归档并清理超过1个月的日志文件: {FileName} -> archive/{ZipName}", fileName, Path.GetFileName(zipPath));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[日志归档] 归档文件 {FileName} 失败", fileName);
                }
            }
        }
    }
}

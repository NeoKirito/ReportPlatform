using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Setup;

public sealed class AgentInstallerService
{
    public const string DefaultTargetFolderName = "PEIS.PrintAgent";
    public const string ProcessName = "PEIS.PrintAgent";
    public const string ExecutableName = "PEIS.PrintAgent.exe";
    public const string ConfigFileName = "config.ini";
    public const string ShortcutName = "PEIS 打印助手.lnk";

    public static string GetDefaultInstallationDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", DefaultTargetFolderName);
    }

    public static AgentInstallerConfig ResolveConfiguration(string[] args)
    {
        AgentInstallerConfig? config = null;

        // 1. 尝试从自身PE末尾读取Overlay配置 (服务端动态写入)
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                config = AgentInstallerPackageHelper.TryExtractConfigFromStream(fs);
            }
        }
        catch { }

        // 2. 尝试从自身文件名解析 (如 PEIS-PrintAgent-Setup_192-168-0-237_82.exe)
        if (config == null)
        {
            try
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                config = AgentInstallerPackageHelper.TryExtractConfigFromFileName(exePath);
            }
            catch { }
        }

        // 3. 命令行参数解析与覆盖
        config ??= new AgentInstallerConfig();
        foreach (var arg in args)
        {
            if (arg.StartsWith("--server-url=", StringComparison.OrdinalIgnoreCase))
                config.ServerUrl = arg["--server-url=".Length..].Trim();
            else if (arg.StartsWith("--station-id=", StringComparison.OrdinalIgnoreCase))
                config.StationId = arg["--station-id=".Length..].Trim();
            else if (arg.StartsWith("--silent-print=", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(arg["--silent-print=".Length..].Trim(), out var b)) config.SilentPrint = b;
            }
            else if (arg.StartsWith("--backend=", StringComparison.OrdinalIgnoreCase))
                config.PrintBackend = arg["--backend=".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            config.ServerUrl = "http://localhost:82";
        }

        return config;
    }

    public static void KillRunningAgentProcesses()
    {
        foreach (var proc in Process.GetProcessesByName(ProcessName))
        {
            try
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(1500))
                {
                    proc.Kill();
                    proc.WaitForExit(1000);
                }
            }
            catch { }
        }
        Thread.Sleep(300);
    }

    public static void ExtractPayload(string targetDirectory, Action<int, string>? progress = null)
    {
        Directory.CreateDirectory(targetDirectory);
        var targetFullPath = Path.GetFullPath(targetDirectory);

        using var stream = GetPayloadStream();
        if (stream == null)
        {
            throw new FileNotFoundException("未在安装程序中找到打印助手组件包 (PrintAgentPayload.zip)，安装包可能已损坏或未打包。");
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var total = archive.Entries.Count;
        var current = 0;

        foreach (var entry in archive.Entries)
        {
            current++;
            var destinationPath = Path.GetFullPath(Path.Combine(targetFullPath, entry.FullName));
            if (!destinationPath.StartsWith(targetFullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"非法压缩路径 (Zip Slip 检测): {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 如果是config.ini且目标已存在，稍后由MergeConfigFile单独合并处理，不直接覆盖
            if (string.Equals(entry.Name, ConfigFileName, StringComparison.OrdinalIgnoreCase) && File.Exists(destinationPath))
            {
                continue;
            }

            entry.ExtractToFile(destinationPath, overwrite: true);
            var pct = 30 + (int)(current * 35.0 / Math.Max(1, total));
            progress?.Invoke(pct, $"正在解压组件: {entry.Name}");
        }
    }

    private static Stream? GetPayloadStream()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceNames = asm.GetManifestResourceNames();
        var zipResource = resourceNames.FirstOrDefault(n => n.EndsWith("PrintAgentPayload.zip", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(zipResource))
        {
            return asm.GetManifestResourceStream(zipResource);
        }

        // 本地开发测试查找：同级目录或资源目录中的 PrintAgentPayload.zip
        var localZip = Path.Combine(AppContext.BaseDirectory, "Resources", "PrintAgentPayload.zip");
        if (File.Exists(localZip)) return File.OpenRead(localZip);

        localZip = Path.Combine(AppContext.BaseDirectory, "PrintAgentPayload.zip");
        if (File.Exists(localZip)) return File.OpenRead(localZip);

        return null;
    }

    public static void MergeConfigFile(string configIniPath, AgentInstallerConfig config)
    {
        var lines = new List<string>();
        var handledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(configIniPath))
        {
            foreach (var rawLine in File.ReadAllLines(configIniPath))
            {
                var line = rawLine.Trim();
                if (line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
                {
                    var sep = line.IndexOf('=');
                    if (sep > 0)
                    {
                        var key = line[..sep].Trim();
                        if (string.Equals(key, "ServerUrl", StringComparison.OrdinalIgnoreCase))
                        {
                            lines.Add($"ServerUrl={config.ServerUrl}");
                            handledKeys.Add("ServerUrl");
                            continue;
                        }
                        if (string.Equals(key, "StationId", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(config.StationId))
                        {
                            lines.Add($"StationId={config.StationId}");
                            handledKeys.Add("StationId");
                            continue;
                        }
                        if (string.Equals(key, "SilentPrint", StringComparison.OrdinalIgnoreCase) && config.SilentPrint.HasValue)
                        {
                            lines.Add($"SilentPrint={config.SilentPrint.Value.ToString().ToLowerInvariant()}");
                            handledKeys.Add("SilentPrint");
                            continue;
                        }
                        if (string.Equals(key, "PrintBackend", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(config.PrintBackend))
                        {
                            lines.Add($"PrintBackend={config.PrintBackend}");
                            handledKeys.Add("PrintBackend");
                            continue;
                        }
                        handledKeys.Add(key);
                    }
                }
                lines.Add(rawLine);
            }
        }

        if (!handledKeys.Contains("ServerUrl"))
            lines.Insert(0, $"ServerUrl={config.ServerUrl}");
        if (!handledKeys.Contains("SilentPrint"))
            lines.Add($"SilentPrint={(config.SilentPrint.HasValue ? config.SilentPrint.Value.ToString().ToLowerInvariant() : "false")}");
        if (!handledKeys.Contains("PrintBackend"))
            lines.Add($"PrintBackend={(!string.IsNullOrWhiteSpace(config.PrintBackend) ? config.PrintBackend : "Spool")}");
        if (!handledKeys.Contains("DefaultPrinter"))
            lines.Add($"DefaultPrinter={config.DefaultPrinter ?? ""}");

        File.WriteAllLines(configIniPath, lines, Encoding.UTF8);
    }

    public static bool TryCreateShortcut(string shortcutPath, string targetExePath, string workingDirectory, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell != null)
                {
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = targetExePath;
                    shortcut.WorkingDirectory = workingDirectory;
                    shortcut.Description = description;
                    shortcut.IconLocation = targetExePath + ",0";
                    shortcut.Save();
                    return true;
                }
            }
        }
        catch
        {
            // 忽略非关键环境下的快捷方式创建异常
        }
        return false;
    }

    public static void CreateShortcuts(string targetExePath, string workingDirectory)
    {
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktopDir) && Directory.Exists(desktopDir))
        {
            var desktopShortcut = Path.Combine(desktopDir, ShortcutName);
            TryCreateShortcut(desktopShortcut, targetExePath, workingDirectory, "PEIS 医疗体检打印助手");
        }

        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (!string.IsNullOrEmpty(startupDir) && Directory.Exists(startupDir))
        {
            var startupShortcut = Path.Combine(startupDir, ShortcutName);
            TryCreateShortcut(startupShortcut, targetExePath, workingDirectory, "PEIS 医疗体检打印助手 (开机自动启动)");
        }
    }

    public static void LaunchInstalledAgent(string targetExePath, string workingDirectory)
    {
        if (File.Exists(targetExePath))
        {
            var psi = new ProcessStartInfo
            {
                FileName = targetExePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
    }

    public static void ExecuteInstall(AgentInstallerConfig config, string? targetDirectory = null, Action<int, string>? progress = null)
    {
        targetDirectory = !string.IsNullOrWhiteSpace(targetDirectory)
            ? targetDirectory
            : GetDefaultInstallationDirectory();

        // 1. 终止旧版进程
        progress?.Invoke(10, "正在检测并终止正在运行的旧版打印助手...");
        KillRunningAgentProcesses();

        // 2. 解压部署文件
        progress?.Invoke(30, "正在部署组件到本地环境...");
        ExtractPayload(targetDirectory, progress);

        // 3. 配置连接
        progress?.Invoke(70, "正在配置服务器连接与打印参数...");
        var configIniPath = Path.Combine(targetDirectory, ConfigFileName);
        MergeConfigFile(configIniPath, config);

        // 4. 创建快捷方式
        progress?.Invoke(85, "正在创建桌面与开机自启动快捷方式...");
        var targetExePath = Path.Combine(targetDirectory, ExecutableName);
        CreateShortcuts(targetExePath, targetDirectory);

        // 5. 启动打印助手
        progress?.Invoke(95, "正在启动 PEIS 打印助手服务...");
        LaunchInstalledAgent(targetExePath, targetDirectory);

        // 6. 完成
        progress?.Invoke(100, "安装成功！打印助手已启动并驻留托盘。");
    }
}

using System.Diagnostics;
using System.Drawing;

namespace PEIS.PrintAgent.Services;

/// <summary>
/// 应用程序图标加载辅助类：
/// 统一管理任务栏托盘、窗口标题栏图标的加载与高清适配。
/// </summary>
internal static class AppIconHelper
{
    /// <summary>
    /// 加载应用程序图标。
    /// 加载顺序：
    /// 1. 外部资源目录（Resources/app.ico、app.ico）
    ///    - 托盘模式（forTray=true）按系统小图标尺寸（16x16/24x24）精准提取多尺寸ICO子图，避免缩放模糊
    ///    - 窗口模式（forTray=false）加载标准32x32图标
    /// 2. 当前运行的可执行文件内嵌PE图标（ExtractAssociatedIcon）
    /// 3. 系统默认应用程序图标兜底（SystemIcons.Application）
    /// </summary>
    public static Icon LoadAppIcon(bool forTray = false)
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "app.ico"),
        };

        foreach (var path in candidatePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var icon = forTray
                        ? new Icon(path, SystemInformation.SmallIconSize)
                        : new Icon(path);

                    if (icon.Size.Width > 0 && icon.Size.Height > 0)
                        return icon;
                }
            }
            catch
            {
                // 图标文件可能损坏或被占用，尝试下一个候选
            }
        }

        // 回退机制：从当前 EXE 二进制中提取内嵌图标（即便外部 Resources 丢失也能显示原生 Logo）
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var exeIcon = Icon.ExtractAssociatedIcon(exePath);
                if (exeIcon is not null && exeIcon.Size.Width > 0)
                {
                    return forTray
                        ? new Icon(exeIcon, SystemInformation.SmallIconSize)
                        : exeIcon;
                }
            }
        }
        catch
        {
            // 忽略提取错误
        }

        return SystemIcons.Application;
    }
}

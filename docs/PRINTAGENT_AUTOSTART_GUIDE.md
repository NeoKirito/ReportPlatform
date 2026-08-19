# PrintAgent 多工作站安装说明

## 适用范围

本说明用于前台、收费、护士站等多台 Windows 电脑同时接收 B/S 静默打印任务的现场部署。每台工作站只运行一个 PrintAgent；浏览器始终只向服务端提交业务动作和站点码，不直接控制 Windows 打印机。

> 请使用发布包中的自包含 `PEIS.PrintAgent.exe`。不要让操作员长期手工打开命令行窗口。

## 一、服务器配置

在 API 的 `appsettings.Production.json` 或安全的环境变量中配置注册令牌。令牌不得提交到代码库或复制到公共共享目录。

```json
{
  "PrintAgentSecurity": {
    "RegistrationToken": "<由运维生成并安全下发的令牌>"
  },
  "PrintAgentRegistry": {
    "OfflineAfterSeconds": 90
  }
}
```

空令牌仅用于兼容旧联调环境。正式现场必须设置非空令牌，并使用 HTTPS 服务地址。

## 二、每台工作站安装

将发布包中的 `print-agent` 目录解压到本机，例如 `C:\PEIS\PrintAgent`。先在 Windows 中安装并测试该电脑需要使用的物理打印机，然后以实际操作员账户打开 PowerShell，执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-PrintAgentAutoStart.ps1 `
  -AgentDirectory 'C:\PEIS\PrintAgent' `
  -StationId 'REG-01' `
  -ServerUrl 'https://<报告服务地址>' `
  -RegistrationToken '<本机受控令牌>'
```

脚本会写入该电脑本地的 `appsettings.Production.json`，并创建名为 `PEIS PrintAgent` 的“用户登录后自动启动”计划任务。首次启动时，代理会在 `%ProgramData%\PEIS\PrintAgent\agent-id.txt` 自动生成稳定 GUID。不要手工复制此文件到另一台电脑。

| 配置项 | 规则 |
|---|---|
| `StationId` | 必须在现场唯一，例如 `REG-01`、`CASH-01`、`NURSE-01`。 |
| `AgentId` | 保持空字符串；由本机首次启动自动生成稳定 GUID。 |
| `RegistrationToken` | 仅保存于本机 Production 配置；不得写入示例配置或版本库。 |
| `PrinterBindings` | 填“逻辑角色 → 本机 Windows 打印机名称”，例如 `A4_GUIDE`、`BARCODE`。 |
| `PrintBackend` | 先使用 `DryRun` 演练；确认无误后配置已验证的真实打印命令。 |

## 三、上线核验

启动 API 后，访问：

```text
GET /api/print/agents
```

应能看到该站点的 `agentId`、`stationId`、计算机名、版本、心跳时间、已安装打印机与逻辑绑定。若同一 `StationId` 被另一台在线电脑占用，新代理会被拒绝注册而不会覆盖旧工作站；应先排查旧电脑、关闭重复任务或分配新的站点码。

随后从 B/S 发起对应站点的打印动作。若任务无法下发，优先检查：工作站是否在线、站点码是否一致、逻辑打印机角色是否已绑定、物理打印机是否在当前登录用户下可见，以及注册令牌是否一致。

## 四、运维说明

代理已具备自动重连、20 秒默认心跳、每物理打印机本地串行队列和有限重试。API 会在默认 90 秒未收到心跳后将该代理视为离线并停止路由。当前代理在线表与打印任务回执仍为单 API 实例内存状态；后续高可用部署应将其迁移到共享持久化存储，并引入代理管理界面、安装包和受控升级。

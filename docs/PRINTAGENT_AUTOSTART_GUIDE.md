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
  "InternalApiSecurity": {
    "AccessToken": "<管理接口令牌>"
  },
  "ArtifactAccess": {
    "SigningKey": "<高熵制品 URL 签名密钥>"
  },
  "PrintAgentRegistry": {
    "OfflineAfterSeconds": 90
  }
}
```

空令牌仅能在明确 Opt-In 的开发环境使用。生产环境会拒绝空注册令牌与未签名制品下载；正式现场必须使用非空令牌、非空制品签名密钥和 HTTPS 服务地址。

## 二、每台工作站安装

将发布包中的 `print-agent` 目录解压到本机，例如 `C:\PEIS\PrintAgent`。先在 Windows 中安装并测试该电脑需要使用的物理打印机，然后以实际操作员账户打开 PowerShell，执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-PrintAgentAutoStart.ps1 `
  -AgentDirectory 'C:\PEIS\PrintAgent' `
  -StationId 'REG-01' `
  -ServerUrl 'https://<报告服务地址>' `
  -RegistrationToken '<本机受控令牌>' `
  -PrinterBindings @{ 'A4_GUIDE' = 'HP LaserJet A4'; 'BARCODE' = 'TSC TE244' }
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
X-PEIS-Internal-Token: <管理接口令牌>
```

应能看到该站点的 `agentId`、`stationId`、计算机名、版本、心跳时间、已安装打印机与逻辑绑定。若同一 `StationId` 被另一台在线电脑占用，新代理会被拒绝注册而不会覆盖旧工作站；应先排查旧电脑、关闭重复任务或分配新的站点码。

随后从 B/S 发起对应站点的打印动作。若任务无法下发，优先检查：工作站是否在线、站点码是否一致、逻辑打印机角色是否已绑定、物理打印机是否在当前登录用户下可见，以及注册令牌是否一致。

## 四、运维说明

代理具备自动重连、20 秒默认心跳、每物理打印机本地串行队列和有限重试。API 会在默认 90 秒未收到心跳后将代理视为离线并停止路由。打印任务、目标状态与幂等键保存在 API 本机 SQLite 状态库中，API 重启后可恢复；在线连接状态会由 Agent 自动重新注册恢复。部署包不提供多节点共享状态，横向扩展前必须替换为共享、受控的持久化实现。
